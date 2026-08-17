// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { nextFire } from "../expression/Evaluator";
import { parseSchedule } from "../expression/Parser";
import { IntervalMode, ScheduleSpec } from "../expression/Spec";
import { Clock, systemClock } from "./Clock";
import { HandlerRegistry, JobContext } from "./HandlerRegistry";
import { JobRun, JobRunRequest } from "./JobRun";
import { JobStore } from "./JobStore";
import { DEFAULT_RETRY_POLICY, PermanentJobError, RetryPolicy, retryDelayMs } from "./RetryPolicy";

export enum MisfirePolicy {
    // Catch up with a single run, whatever was missed. The right default: after
    // an outage you usually want the job to happen, once, not sixty times.
    FireOnce = "fire_once",
    // Enqueue every missed occurrence.
    FireAll = "fire_all",
    // Abandon what was missed and wait for the next occurrence.
    Skip = "skip"
}

export interface ScheduleDefinition {
    // Unique. Also forms the idempotency key of every run it materializes, so
    // two workers materializing the same occurrence produce the same key and
    // exactly one insert survives.
    readonly name: string;
    readonly expr: string;
    readonly handler: string;
    readonly payload?: unknown;
    readonly maxAttempts?: number;
    readonly misfire?: MisfirePolicy;
}

export interface WorkerOptions {
    readonly store: JobStore;
    readonly registry: HandlerRegistry;
    readonly clock?: Clock;
    // Identifies this worker in lease records. Defaults to a random label.
    readonly owner?: string;
    readonly concurrency?: number;
    readonly pollIntervalMs?: number;
    readonly leaseMs?: number;
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
}

interface TrackedSchedule {
    readonly def: ScheduleDefinition;
    readonly spec: ScheduleSpec;
    // Null means the next occurrence is chained on settle rather than
    // materialized on a tick.
    nextFireAtMs: number | null;
    readonly chainOnSettle: boolean;
}

// Guards against enumerating an unbounded number of missed occurrences when a
// fast schedule has been down for a long time.
const MAX_CATCH_UP = 10_000;

export class Worker {
    private readonly store: JobStore;
    private readonly registry: HandlerRegistry;
    private readonly clock: Clock;
    private readonly owner: string;
    private readonly concurrency: number;
    private readonly pollIntervalMs: number;
    private readonly leaseMs: number;
    private readonly retry: RetryPolicy;
    private readonly random: () => number;
    private readonly onError: (error: unknown, run: JobRun | null) => void;

    private readonly schedules = new Map<string, TrackedSchedule>();
    private readonly stopController = new AbortController();
    private loop: Promise<void> | null = null;

    constructor(options: WorkerOptions) {
        this.store = options.store;
        this.registry = options.registry;
        this.clock = options.clock ?? systemClock;
        this.owner = options.owner ?? `worker-${Math.random().toString(36).slice(2, 10)}`;
        this.concurrency = Math.max(1, options.concurrency ?? 4);
        this.pollIntervalMs = Math.max(1, options.pollIntervalMs ?? 1_000);
        this.leaseMs = Math.max(1, options.leaseMs ?? 30_000);
        this.retry = options.retry ?? DEFAULT_RETRY_POLICY;
        this.random = options.random ?? Math.random;
        this.onError = options.onError ?? (() => { });
    }

    // Registers a recurring schedule. Safe to call before or after start.
    addSchedule(def: ScheduleDefinition): void {
        if (this.schedules.has(def.name)) {
            throw new Error(`schedule '${def.name}' is already registered`);
        }

        const spec = parseSchedule(def.expr);

        // Fixed delay measures from the end of the previous run, so its next
        // occurrence is only knowable once the current one settles. Every other
        // kind sits on a timeline the materializer can walk ahead of time.
        const chainOnSettle = spec.kind === "interval" && spec.mode === IntervalMode.FixedDelay;

        this.schedules.set(def.name, {
            def,
            spec,
            nextFireAtMs: nextFire(spec, this.clock.nowMs()),
            chainOnSettle
        });
    }

    // Queues a one off job.
    async enqueue(
        handler: string,
        payload?: unknown,
        options?: { runAtMs?: number; maxAttempts?: number; idempotencyKey?: string }
    ): Promise<JobRun | null> {
        return this.store.enqueue({
            handler,
            payload,
            runAtMs: options?.runAtMs ?? this.clock.nowMs(),
            maxAttempts: options?.maxAttempts ?? this.retry.maxAttempts,
            idempotencyKey: options?.idempotencyKey ?? null
        });
    }

    // One pass of the loop. Exposed so tests can drive the worker with a fake
    // clock and assert on each step instead of racing real timers.
    async tick(): Promise<TickResult> {
        const now = this.clock.nowMs();

        const reaped = await this.store.reapExpired(now);
        const materialized = await this.materialize(now);
        const claimed = await this.store.claimDue(this.owner, now, this.leaseMs, this.concurrency);

        let succeeded = 0;
        let retried = 0;
        let dead = 0;

        const outcomes = await Promise.all(claimed.map(run => this.dispatch(run)));
        for (const outcome of outcomes) {
            if (outcome === "succeeded") succeeded += 1;
            else if (outcome === "retried") retried += 1;
            else dead += 1;
        }

        return { reaped, materialized, claimed: claimed.length, succeeded, retried, dead };
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
                await this.clock.sleep(this.pollIntervalMs, this.stopController.signal);
            }
        })();
    }

    // Stops claiming and waits for the current pass to finish. In flight
    // handlers see their signal abort. Anything still leased is left to the
    // reaper, which is why handlers have to be idempotent.
    async stop(): Promise<void> {
        this.stopController.abort();
        const loop = this.loop;
        this.loop = null;
        if (loop !== null) await loop;
    }

    private async materialize(nowMs: number): Promise<number> {
        let materialized = 0;

        for (const tracked of this.schedules.values()) {
            if (tracked.nextFireAtMs === null) continue;

            // Walk the occurrences that have come due since the last pass.
            const due: number[] = [];
            let cursor: number | null = tracked.nextFireAtMs;

            while (cursor !== null && cursor <= nowMs && due.length < MAX_CATCH_UP) {
                due.push(cursor);
                cursor = nextFire(tracked.spec, cursor);
            }

            if (due.length === 0) continue;

            const misfire = tracked.def.misfire ?? MisfirePolicy.FireOnce;
            const toEnqueue =
                misfire === MisfirePolicy.FireAll ? due :
                    misfire === MisfirePolicy.Skip ? [] :
                        [due[due.length - 1]];

            for (const fireAt of toEnqueue) {
                const run = await this.store.enqueue(this.requestFor(tracked, fireAt));
                if (run !== null) materialized += 1;
            }

            // A chained schedule re-arms when its run settles, not here.
            tracked.nextFireAtMs = tracked.chainOnSettle ? null : cursor;
        }

        return materialized;
    }

    private requestFor(tracked: TrackedSchedule, fireAtMs: number): JobRunRequest {
        return {
            handler: tracked.def.handler,
            payload: tracked.def.payload,
            // Jitter moves when the run happens but not its identity. Keying on
            // the jittered time would let two workers compute different keys for
            // the same occurrence and enqueue it twice.
            runAtMs: fireAtMs + this.jitterFor(tracked.spec),
            scheduleId: tracked.def.name,
            idempotencyKey: `${tracked.def.name}:${fireAtMs}`,
            maxAttempts: tracked.def.maxAttempts ?? this.retry.maxAttempts
        };
    }

    private jitterFor(spec: ScheduleSpec): number {
        if (spec.kind !== "interval" || spec.jitterMs <= 0) return 0;
        return Math.round(this.random() * spec.jitterMs);
    }

    // The successor to enqueue in the same call that settles this run. Null for
    // one off work and for schedules the materializer already walks forward.
    private chainFor(run: JobRun, nowMs: number): JobRunRequest | null {
        if (run.scheduleId === null) return null;

        const tracked = this.schedules.get(run.scheduleId);
        if (tracked === undefined || !tracked.chainOnSettle) return null;

        const fireAt = nextFire(tracked.spec, nowMs);
        if (fireAt === null) return null;

        return this.requestFor(tracked, fireAt);
    }

    private async dispatch(run: JobRun): Promise<"succeeded" | "retried" | "dead"> {
        const handler = this.registry.resolve(run.handler);

        if (handler === undefined) {
            // Retrying cannot help in this process. A durable multi process
            // deployment should instead claim only handlers it knows about.
            const at = this.clock.nowMs();
            const error = `no handler registered for '${run.handler}'`;
            await this.store.deadLetter(run.id, error, this.chainFor(run, at), at);
            this.onError(new Error(error), run);
            return "dead";
        }

        const ctx: JobContext = {
            runId: run.id,
            attempt: run.attempt,
            maxAttempts: run.maxAttempts,
            signal: this.stopController.signal,
            heartbeat: () => this.store.heartbeat(run.id, this.clock.nowMs() + this.leaseMs)
        };

        try {
            await handler(run.payload, ctx);
        } catch (error) {
            return this.settleFailure(run, error);
        }

        const at = this.clock.nowMs();
        await this.store.complete(run.id, this.chainFor(run, at), at);
        return "succeeded";
    }

    private async settleFailure(run: JobRun, error: unknown): Promise<"retried" | "dead"> {
        const at = this.clock.nowMs();
        const message = error instanceof Error ? error.message : String(error);
        this.onError(error, run);

        const permanent = error instanceof PermanentJobError;
        if (!permanent && run.attempt < run.maxAttempts) {
            const delay = retryDelayMs(this.retry, run.attempt, this.random);
            await this.store.retry(run.id, message, at + delay, at);
            return "retried";
        }

        await this.store.deadLetter(run.id, message, this.chainFor(run, at), at);
        return "dead";
    }
}
