// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { nextFire } from "../expression/Evaluator";
import { parseSchedule } from "../expression/Parser";
import { IntervalMode, ScheduleSpec } from "../expression/Spec";
import { Clock, systemClock } from "./Clock";
import { HandlerRegistry, JobContext } from "./HandlerRegistry";
import { JobDefinition, PayloadError } from "./JobDefinition";
import { JobRun, JobRunRequest } from "./JobRun";
import { JobNotifier } from "./JobNotifier";
import { JobObserver, RunOutcome } from "./JobObserver";
import { JobStore, JobStoreStats } from "./JobStore";
import { MisfirePolicy } from "./MisfirePolicy";
export { MisfirePolicy };
import { InMemoryScheduleStore } from "./InMemoryScheduleStore";
import { ScheduleRecord, ScheduleStore } from "./ScheduleStore";
import { DEFAULT_RETRY_POLICY, PermanentJobError, RetryPolicy, retryDelayMs } from "./RetryPolicy";

export interface ScheduleDefinition<TPayload = void> {
    // Unique. Also forms the idempotency key of every run it materializes, so
    // two workers materializing the same occurrence produce the same key and
    // exactly one insert survives.
    readonly name: string;
    readonly expr: string;
    // The job to run. Pass the definition rather than its name so the payload
    // below is checked against it.
    readonly job: JobDefinition<TPayload>;
    readonly payload?: TPayload;
    readonly maxAttempts?: number;
    readonly misfire?: MisfirePolicy;
}

export interface EnqueueOptions {
    readonly runAtMs?: number;
    readonly maxAttempts?: number;
    readonly idempotencyKey?: string;
}

export interface RetentionPolicy {
    // Settled runs older than this are deleted.
    readonly afterMs: number;
    // How often to sweep.
    readonly everyMs: number;
    // Rows per sweep, so a long backlog clears over several passes.
    readonly batch?: number;
    // Off by default. A dead run is evidence that something never ran, and
    // deleting it loses the only record of that.
    readonly includeDead?: boolean;
}

export interface WorkerOptions {
    readonly store: JobStore;
    // Jobs to register. Either give these and let the worker build a registry,
    // or bring your own registry, or both.
    readonly jobs?: readonly JobDefinition<any>[];
    readonly registry?: HandlerRegistry;
    // Recurring schedules to register up front. Registering touches the
    // schedule store, so these are applied by createScheduler rather than the
    // constructor.
    readonly schedules?: readonly ScheduleDefinition<any>[];
    // Where schedules live. Defaults to memory, which is right when schedules
    // are declared in code. Give it a PostgresScheduleStore to have a pause
    // survive a restart, or to add a schedule without a deploy.
    readonly scheduleStore?: ScheduleStore;
    readonly clock?: Clock;
    // Identifies this worker in lease records. Defaults to a random label.
    readonly owner?: string;
    readonly concurrency?: number;
    readonly pollIntervalMs?: number;
    readonly leaseMs?: number;
    // How often start renews the leases of in flight runs. Defaults to a third
    // of leaseMs, so two renewals can be missed before a lease lapses.
    readonly heartbeatMs?: number;
    // Claim only runs whose handler is registered here. On by default, so in a
    // mixed fleet a run is left for a process that can execute it. Turn off to
    // have this worker claim everything and dead letter what it cannot run.
    readonly claimOnlyRegisteredHandlers?: boolean;
    // Optional. Lets a worker be woken when work arrives instead of waiting out
    // pollIntervalMs. Polling stays the floor either way.
    readonly notifier?: JobNotifier;
    // Somewhere to hang a log line, a metric or a trace span. See JobObserver.
    readonly observer?: JobObserver;
    readonly retention?: RetentionPolicy;
    readonly retry?: RetryPolicy;
    readonly random?: () => number;
    readonly onError?: (error: unknown, run: JobRun | null) => void;
}

export interface TickResult {
    readonly reaped: number;
    readonly materialized: number;
    readonly claimed: number;
    readonly succeeded: number;
    readonly retried: number;
    readonly dead: number;
    readonly purged: number;
}

// Guards against enumerating an unbounded number of missed occurrences when a
// fast schedule has been down for a long time.
const MAX_CATCH_UP = 10_000;

// A tick materializes at most this many schedules, so one pass stays bounded no
// matter how many are registered.
const MAX_SCHEDULES_PER_TICK = 1_000;

// Fixed delay measures from the end of the previous run, so its next occurrence
// is only knowable once the current one settles. Every other kind sits on a
// timeline the materializer can walk ahead of time.
function chainsOnSettle(spec: ScheduleSpec): boolean {
    return spec.kind === "interval" && spec.mode === IntervalMode.FixedDelay;
}

export class Worker {
    private readonly store: JobStore;
    private readonly registry: HandlerRegistry;
    private readonly clock: Clock;
    private readonly owner: string;
    private readonly concurrency: number;
    private readonly pollIntervalMs: number;
    private readonly leaseMs: number;
    private readonly heartbeatMs: number;
    private readonly claimOnlyRegisteredHandlers: boolean;
    private readonly notifier: JobNotifier | null;
    private readonly observer: JobObserver | null;
    private readonly retention: RetentionPolicy | null;
    private readonly retry: RetryPolicy;
    private readonly random: () => number;
    private readonly onError: (error: unknown, run: JobRun | null) => void;

    private readonly scheduleStore: ScheduleStore;
    private readonly inFlight = new Set<string>();
    private readonly stopController = new AbortController();
    private loop: Promise<void> | null = null;
    private renewal: Promise<void> | null = null;
    private nextPurgeAtMs = 0;

    constructor(options: WorkerOptions) {
        this.store = options.store;
        this.registry = options.registry ?? new HandlerRegistry();
        this.scheduleStore = options.scheduleStore ?? new InMemoryScheduleStore();
        if (options.jobs !== undefined) this.registry.registerAll(options.jobs);
        this.clock = options.clock ?? systemClock;
        this.owner = options.owner ?? `worker-${Math.random().toString(36).slice(2, 10)}`;
        this.concurrency = Math.max(1, options.concurrency ?? 4);
        this.pollIntervalMs = Math.max(1, options.pollIntervalMs ?? 1_000);
        this.leaseMs = Math.max(1, options.leaseMs ?? 30_000);
        this.heartbeatMs = Math.max(1, options.heartbeatMs ?? Math.floor(this.leaseMs / 3));
        this.claimOnlyRegisteredHandlers = options.claimOnlyRegisteredHandlers ?? true;
        this.notifier = options.notifier ?? null;
        this.observer = options.observer ?? null;
        this.retention = options.retention ?? null;
        this.retry = options.retry ?? DEFAULT_RETRY_POLICY;
        this.random = options.random ?? Math.random;
        this.onError = options.onError ?? (() => { });
    }

    // Registers a recurring schedule, or updates one that already exists.
    // Re-registering keeps whether it is enabled, so a redeploy cannot silently
    // resume something an operator paused, and keeps its place in time unless
    // the expression itself changed.
    async addSchedule<TPayload>(def: ScheduleDefinition<TPayload>): Promise<ScheduleRecord> {
        if (!this.registry.has(def.job.name)) this.registry.register(def.job);

        const spec = parseSchedule(def.expr);
        const now = this.clock.nowMs();

        return this.scheduleStore.upsert({
            name: def.name,
            handler: def.job.name,
            payload: def.payload,
            expr: def.expr,
            spec,
            misfire: def.misfire ?? MisfirePolicy.FireOnce,
            maxAttempts: def.maxAttempts ?? def.job.maxAttempts ?? null,
            nextFireAtMs: nextFire(spec, now)
        }, now);
    }

    // Admin surface.

    listSchedules(): Promise<ScheduleRecord[]> {
        return this.scheduleStore.list();
    }

    getSchedule(name: string): Promise<ScheduleRecord | null> {
        return this.scheduleStore.get(name);
    }

    pauseSchedule(name: string): Promise<boolean> {
        return this.scheduleStore.setEnabled(name, false, this.clock.nowMs());
    }

    resumeSchedule(name: string): Promise<boolean> {
        return this.scheduleStore.setEnabled(name, true, this.clock.nowMs());
    }

    removeSchedule(name: string): Promise<boolean> {
        return this.scheduleStore.remove(name);
    }

    // Runs a schedule now without disturbing its timetable. The key is distinct
    // from a materialized occurrence, so triggering twice in the same
    // millisecond is the only way to collide, and a paused schedule can still be
    // triggered on purpose.
    async triggerSchedule(name: string): Promise<JobRun | null> {
        const record = await this.scheduleStore.get(name);
        if (record === null) return null;

        const now = this.clock.nowMs();
        return this.store.enqueue({
            handler: record.handler,
            payload: record.payload,
            runAtMs: now,
            scheduleId: record.name,
            idempotencyKey: `${record.name}:manual:${now}`,
            maxAttempts: record.maxAttempts ?? this.retry.maxAttempts
        });
    }

    cancelRun(runId: string): Promise<boolean> {
        return this.store.cancel(runId, this.clock.nowMs());
    }

    requeueRun(runId: string, runAtMs?: number): Promise<boolean> {
        const now = this.clock.nowMs();
        return this.store.requeue(runId, runAtMs ?? now, now);
    }

    // Queues a one off job. Pass the definition, not its name, so the payload is
    // checked against the handler that will receive it.
    enqueue(
        job: JobDefinition<void>, payload?: undefined, options?: EnqueueOptions
    ): Promise<JobRun | null>;
    enqueue<TPayload>(
        job: JobDefinition<TPayload>, payload: TPayload, options?: EnqueueOptions
    ): Promise<JobRun | null>;
    enqueue<TPayload>(
        job: JobDefinition<TPayload>, payload?: TPayload, options?: EnqueueOptions
    ): Promise<JobRun | null> {
        if (!this.registry.has(job.name)) this.registry.register(job);
        return this.enqueueByName(job.name, payload, {
            maxAttempts: job.maxAttempts,
            ...options
        });
    }

    // Escape hatch for queueing a job whose definition is not to hand, for
    // example from an admin endpoint that takes a name off a request.
    async enqueueByName(
        handler: string, payload?: unknown, options?: EnqueueOptions
    ): Promise<JobRun | null> {
        const runAtMs = options?.runAtMs ?? this.clock.nowMs();

        const run = await this.store.enqueue({
            handler,
            payload,
            runAtMs,
            maxAttempts: options?.maxAttempts ?? this.retry.maxAttempts,
            idempotencyKey: options?.idempotencyKey ?? null
        });

        // Only worth waking anyone for work that is already due. Anything later
        // will be found by the poll that covers it.
        if (run !== null && runAtMs <= this.clock.nowMs()) await this.announce();
        return run;
    }

    // Never throws. A worker that cannot announce new work still enqueued it.
    private async announce(): Promise<void> {
        if (this.notifier === null) return;

        try {
            await this.notifier.notify();
        } catch (error) {
            this.onError(error, null);
        }
    }

    private async idle(): Promise<void> {
        if (this.notifier === null) {
            await this.clock.sleep(this.pollIntervalMs, this.stopController.signal);
            return;
        }

        try {
            await this.notifier.wait(this.pollIntervalMs, this.stopController.signal);
        } catch (error) {
            this.onError(error, null);
            await this.clock.sleep(this.pollIntervalMs, this.stopController.signal);
        }
    }

    // One pass of the loop. Exposed so tests can drive the worker with a fake
    // clock and assert on each step instead of racing real timers.
    async tick(): Promise<TickResult> {
        const now = this.clock.nowMs();
        const startedAt = Date.now();

        const reaped = await this.store.reapExpired(now);
        const purged = await this.purge(now);
        const materialized = await this.materialize(now);

        const claimed = await this.store.claimDue(
            this.owner, now, this.leaseMs, this.concurrency,
            this.claimOnlyRegisteredHandlers ? this.registry.names() : undefined);

        let succeeded = 0;
        let retried = 0;
        let dead = 0;

        const outcomes = await Promise.all(claimed.map(run => this.dispatch(run)));
        for (const outcome of outcomes) {
            if (outcome === "succeeded") succeeded += 1;
            else if (outcome === "retried") retried += 1;
            else dead += 1;
        }

        const result = {
            reaped, materialized, claimed: claimed.length, succeeded, retried, dead, purged
        };

        this.observe(o => o.tickFinished?.({ result, durationMs: Date.now() - startedAt }));
        return result;
    }

    // Observing work must never be able to break it, so a callback that throws
    // is reported and otherwise ignored.
    private observe(emit: (observer: JobObserver) => void): void {
        if (this.observer === null) return;

        try {
            emit(this.observer);
        } catch (error) {
            this.onError(error, null);
        }
    }

    // Counts by status plus the age of the oldest waiting run, for feeding
    // metrics. Alert on oldestPendingAgeMs.
    stats(): Promise<JobStoreStats> {
        return this.store.stats(this.clock.nowMs());
    }

    start(): void {
        if (this.loop !== null) return;

        this.loop = (async () => {
            while (!this.stopController.signal.aborted) {
                try {
                    await this.tick();
                } catch (error) {
                    this.onError(error, null);
                }
                await this.idle();
            }
        })();

        this.renewal = this.renewLeases();
    }

    // Stops claiming and waits for the current pass to finish. In flight
    // handlers see their signal abort. Anything still leased is left to the
    // reaper, which is why handlers have to be idempotent.
    async stop(): Promise<void> {
        this.stopController.abort();

        const loop = this.loop;
        const renewal = this.renewal;
        this.loop = null;
        this.renewal = null;

        if (loop !== null) await loop;
        if (renewal !== null) await renewal;
    }

    // Keeps the leases of in flight runs alive for as long as their handlers are
    // working, so a handler that outlives leaseMs is not reaped and run twice by
    // another worker. Runs alongside the tick loop rather than inside it,
    // because a tick is blocked on the very handlers that need renewing.
    private renewLeases(): Promise<void> {
        return (async () => {
            while (!this.stopController.signal.aborted) {
                await this.clock.sleep(this.heartbeatMs, this.stopController.signal);
                if (this.stopController.signal.aborted) break;
                if (this.inFlight.size === 0) continue;

                const until = this.clock.nowMs() + this.leaseMs;
                for (const runId of Array.from(this.inFlight)) {
                    try {
                        // A lost lease means the reaper already gave the run to
                        // someone else. Stop renewing, the handler's own
                        // heartbeat call will tell it to stop.
                        if (!await this.store.heartbeat(runId, until)) this.inFlight.delete(runId);
                    } catch (error) {
                        this.onError(error, null);
                    }
                }
            }
        })();
    }

    private async purge(nowMs: number): Promise<number> {
        if (this.retention === null || nowMs < this.nextPurgeAtMs) return 0;

        this.nextPurgeAtMs = nowMs + this.retention.everyMs;
        return this.store.purgeSettled(
            nowMs - this.retention.afterMs,
            this.retention.batch ?? 1_000,
            this.retention.includeDead ?? false);
    }

    private async materialize(nowMs: number): Promise<number> {
        let materialized = 0;

        for (const record of await this.scheduleStore.listDue(nowMs, MAX_SCHEDULES_PER_TICK)) {
            if (record.nextFireAtMs === null) continue;

            // Walk the occurrences that have come due since the last pass.
            const due: number[] = [];
            let cursor: number | null = record.nextFireAtMs;

            while (cursor !== null && cursor <= nowMs && due.length < MAX_CATCH_UP) {
                due.push(cursor);
                cursor = nextFire(record.spec, cursor);
            }

            if (due.length === 0) continue;

            const toEnqueue =
                record.misfire === MisfirePolicy.FireAll ? due :
                    record.misfire === MisfirePolicy.Skip ? [] :
                        [due[due.length - 1]];

            for (const fireAt of toEnqueue) {
                // Enqueue before advancing. The key makes the insert idempotent,
                // so a crash in between costs a repeated attempt rather than a
                // lost occurrence.
                const run = await this.store.enqueue(this.requestFor(record, fireAt));
                if (run === null) continue;

                materialized += 1;
                if (run.runAtMs <= nowMs) await this.announce();
            }

            // A chained schedule re-arms when its run settles, not here.
            await this.scheduleStore.advance(
                record.name,
                chainsOnSettle(record.spec) ? null : cursor,
                due[due.length - 1],
                nowMs);
        }

        return materialized;
    }

    private requestFor(record: ScheduleRecord, fireAtMs: number): JobRunRequest {
        return {
            handler: record.handler,
            payload: record.payload,
            // Jitter moves when the run happens but not its identity. Keying on
            // the jittered time would let two workers compute different keys for
            // the same occurrence and enqueue it twice.
            runAtMs: fireAtMs + this.jitterFor(record.spec),
            scheduleId: record.name,
            idempotencyKey: `${record.name}:${fireAtMs}`,
            maxAttempts: record.maxAttempts ?? this.retry.maxAttempts
        };
    }

    private jitterFor(spec: ScheduleSpec): number {
        if (spec.kind !== "interval" || spec.jitterMs <= 0) return 0;
        return Math.round(this.random() * spec.jitterMs);
    }

    // The successor to enqueue in the same call that settles this run. Null for
    // one off work and for schedules the materializer already walks forward.
    private async chainFor(run: JobRun, nowMs: number): Promise<JobRunRequest | null> {
        if (run.scheduleId === null) return null;

        const record = await this.scheduleStore.get(run.scheduleId);
        if (record === null || !record.enabled || !chainsOnSettle(record.spec)) return null;

        const fireAt = nextFire(record.spec, nowMs);
        if (fireAt === null) return null;

        return this.requestFor(record, fireAt);
    }

    private async dispatch(run: JobRun): Promise<"succeeded" | "retried" | "dead"> {
        const job = this.registry.resolve(run.handler);

        if (job === undefined) {
            // Retrying cannot help in this process. A durable multi process
            // deployment should instead claim only handlers it knows about.
            const at = this.clock.nowMs();
            const message = `no handler registered for '${run.handler}'`;
            const error = new Error(message);

            await this.store.deadLetter(run.id, message, await this.chainFor(run, at), at);
            this.onError(error, run);
            this.observe(o => o.runFinished?.({ run, outcome: "dead", durationMs: 0, error }));
            return "dead";
        }

        const ctx: JobContext = {
            runId: run.id,
            attempt: run.attempt,
            maxAttempts: run.maxAttempts,
            signal: this.stopController.signal,
            heartbeat: () => this.store.heartbeat(run.id, this.clock.nowMs() + this.leaseMs)
        };

        this.observe(o => o.runStarted?.({ run, atMs: this.clock.nowMs() }));
        const startedAt = Date.now();

        let payload: unknown;
        try {
            // Validating on dequeue rather than on enqueue is what catches a
            // payload written by an older deploy meeting a handler that has
            // since changed shape.
            payload = job.parse === undefined ? run.payload : job.parse(run.payload);
        } catch (error) {
            // Retrying cannot change what is already stored.
            return this.settleFailure(run, new PayloadError(job.name, error), startedAt, true);
        }

        this.inFlight.add(run.id);
        try {
            await job.handler(payload, ctx);
        } catch (error) {
            return this.settleFailure(run, error, startedAt);
        } finally {
            this.inFlight.delete(run.id);
        }

        const at = this.clock.nowMs();
        await this.store.complete(run.id, await this.chainFor(run, at), at);

        this.observe(o => o.runFinished?.({
            run, outcome: "succeeded", durationMs: Date.now() - startedAt
        }));
        return "succeeded";
    }

    private async settleFailure(
        run: JobRun, error: unknown, startedAt: number, forcePermanent = false
    ): Promise<"retried" | "dead"> {
        const at = this.clock.nowMs();
        const message = error instanceof Error ? error.message : String(error);
        this.onError(error, run);

        const finish = (outcome: RunOutcome, nextAttemptAtMs: number | null) =>
            this.observe(o => o.runFinished?.({
                run, outcome, error, nextAttemptAtMs, durationMs: Date.now() - startedAt
            }));

        const permanent = forcePermanent || error instanceof PermanentJobError;
        if (!permanent && run.attempt < run.maxAttempts) {
            const delay = retryDelayMs(this.retry, run.attempt, this.random);
            await this.store.retry(run.id, message, at + delay, at);

            finish("retried", at + delay);
            return "retried";
        }

        await this.store.deadLetter(run.id, message, await this.chainFor(run, at), at);

        finish("dead", null);
        return "dead";
    }
}
