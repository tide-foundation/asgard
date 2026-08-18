// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Requires: npm run build:cjs

const { test, describe } = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const {
    FakeClock, InMemoryJobStore, HandlerRegistry, Worker, MisfirePolicy,
    JobStatus, JitterMode, retryDelayMs, shouldRetry, PermanentJobError,
    defineJob, createScheduler, InMemoryScheduleStore, InMemoryNotifier
} = require(path.join(__dirname, "..", "..", "dist", "cjs", "scheduler", "index.js"));

const T0 = Date.parse("2026-08-17T00:00:00Z");

// Fixed backoff so a run lands on a predictable instant.
const NO_JITTER = {
    maxAttempts: 3, baseMs: 1_000, capMs: 60_000, multiplier: 2, jitter: JitterMode.None
};

// Jobs register themselves when enqueued or scheduled, so tests only name a
// job once, in its definition.
const job = (name, handler, extra = {}) => defineJob({ name, handler, ...extra });

function harness(options = {}) {
    const clock = new FakeClock(T0);
    const store = new InMemoryJobStore();
    const registry = new HandlerRegistry();
    const worker = new Worker({
        store,
        registry,
        clock,
        owner: "test",
        leaseMs: 30_000,
        retry: NO_JITTER,
        random: () => 0.5,
        ...options
    });
    return { clock, store, registry, worker };
}

describe("retry backoff", () => {
    test("doubles from the base delay", () => {
        assert.equal(retryDelayMs(NO_JITTER, 1), 1_000);
        assert.equal(retryDelayMs(NO_JITTER, 2), 2_000);
        assert.equal(retryDelayMs(NO_JITTER, 3), 4_000);
    });

    test("caps before jitter is applied", () => {
        const policy = { ...NO_JITTER, capMs: 3_000 };
        assert.equal(retryDelayMs(policy, 9), 3_000);
    });

    test("full jitter spans zero to the delay", () => {
        const policy = { ...NO_JITTER, jitter: JitterMode.Full };
        assert.equal(retryDelayMs(policy, 1, () => 0), 0);
        assert.equal(retryDelayMs(policy, 1, () => 1), 1_000);
        assert.equal(retryDelayMs(policy, 1, () => 0.5), 500);
    });

    test("equal jitter keeps half the delay", () => {
        const policy = { ...NO_JITTER, jitter: JitterMode.Equal };
        assert.equal(retryDelayMs(policy, 1, () => 0), 500);
        assert.equal(retryDelayMs(policy, 1, () => 1), 1_000);
    });

    test("shouldRetry counts attempts already started", () => {
        assert.equal(shouldRetry(NO_JITTER, 2), true);
        assert.equal(shouldRetry(NO_JITTER, 3), false);
    });
});

describe("in memory store", () => {
    test("enqueue then claim leases the run and counts the attempt", async () => {
        const store = new InMemoryJobStore();
        await store.enqueue({ handler: "h", runAtMs: T0, maxAttempts: 3 });

        const claimed = await store.claimDue("worker-a", T0, 30_000, 10);
        assert.equal(claimed.length, 1);
        assert.equal(claimed[0].status, JobStatus.Leased);
        assert.equal(claimed[0].attempt, 1);
        assert.equal(claimed[0].leaseOwner, "worker-a");
        assert.equal(claimed[0].leaseExpiresAtMs, T0 + 30_000);
    });

    test("a leased run is not handed to a second claimer", async () => {
        const store = new InMemoryJobStore();
        await store.enqueue({ handler: "h", runAtMs: T0 });

        assert.equal((await store.claimDue("worker-a", T0, 30_000, 10)).length, 1);
        assert.equal((await store.claimDue("worker-b", T0, 30_000, 10)).length, 0);
    });

    test("runs in the future are not claimed", async () => {
        const store = new InMemoryJobStore();
        await store.enqueue({ handler: "h", runAtMs: T0 + 5_000 });

        assert.equal((await store.claimDue("worker-a", T0, 30_000, 10)).length, 0);
        assert.equal((await store.claimDue("worker-a", T0 + 5_000, 30_000, 10)).length, 1);
    });

    test("a repeated idempotency key is discarded", async () => {
        const store = new InMemoryJobStore();
        const first = await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "nightly:1" });
        const second = await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "nightly:1" });

        assert.notEqual(first, null);
        assert.equal(second, null);
        assert.equal(store.all().length, 1);
    });

    test("an expired lease returns the run to pending", async () => {
        const store = new InMemoryJobStore();
        await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.claimDue("worker-a", T0, 1_000, 10);

        assert.equal(await store.reapExpired(T0 + 500), 0);
        assert.equal(await store.reapExpired(T0 + 1_001), 1);
        assert.equal(store.countByStatus(JobStatus.Pending), 1);
    });

    test("heartbeat pushes the lease out and fails once the run is gone", async () => {
        const store = new InMemoryJobStore();
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.claimDue("worker-a", T0, 1_000, 10);

        assert.equal(await store.heartbeat(run.id, T0 + 60_000), true);
        assert.equal(await store.reapExpired(T0 + 1_001), 0);

        await store.complete(run.id, null, T0 + 2_000);
        assert.equal(await store.heartbeat(run.id, T0 + 90_000), false);
    });

    test("complete chains the successor in the same call", async () => {
        const store = new InMemoryJobStore();
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.complete(run.id, { handler: "h", runAtMs: T0 + 60_000 }, T0);

        assert.equal(store.countByStatus(JobStatus.Succeeded), 1);
        assert.equal(store.countByStatus(JobStatus.Pending), 1);
    });
});

describe("worker: one off jobs", () => {
    test("runs a job and passes the payload through", async () => {
        const { store, worker } = harness();
        const seen = [];
        const greet = job("greet", async (payload, ctx) => {
            seen.push({ payload, attempt: ctx.attempt });
        });

        await worker.enqueue(greet, { name: "asgard" });
        const result = await worker.tick();

        assert.equal(result.claimed, 1);
        assert.equal(result.succeeded, 1);
        assert.deepEqual(seen, [{ payload: { name: "asgard" }, attempt: 1 }]);
        assert.equal(store.countByStatus(JobStatus.Succeeded), 1);
    });

    test("a job scheduled for later is not run yet", async () => {
        const { clock, worker } = harness();
        let runs = 0;
        const later = job("later", () => { runs += 1; });

        await worker.enqueue(later, undefined, { runAtMs: T0 + 10_000 });
        assert.equal((await worker.tick()).claimed, 0);

        clock.advance(10_000);
        assert.equal((await worker.tick()).succeeded, 1);
        assert.equal(runs, 1);
    });

    test("a run for an unregistered handler is left alone, not claimed", async () => {
        const { store, worker } = harness();
        await worker.enqueueByName("missing");

        const result = await worker.tick();
        assert.equal(result.claimed, 0, "another worker may be able to run it");
        assert.equal(store.countByStatus(JobStatus.Pending), 1);
    });

    test("only registered handlers are claimed when others are due", async () => {
        const { store, worker } = harness();
        const known = job("known", () => { });

        await worker.enqueueByName("missing");
        await worker.enqueue(known);

        const result = await worker.tick();
        assert.equal(result.claimed, 1);
        assert.equal(result.succeeded, 1);
        assert.equal(store.countByStatus(JobStatus.Pending), 1, "the unknown one still waits");
    });

    test("with filtering off an unknown handler goes to dead", async () => {
        const { store, worker } = harness({ claimOnlyRegisteredHandlers: false });
        await worker.enqueueByName("missing");

        assert.equal((await worker.tick()).dead, 1);

        const [run] = store.byStatus(JobStatus.Dead);
        assert.match(run.lastError, /no handler registered/);
    });
});

describe("job definitions", () => {
    test("enqueueing a definition registers it", async () => {
        const { registry, worker } = harness();
        const greet = job("greet", () => { });

        assert.equal(registry.has("greet"), false);
        await worker.enqueue(greet);
        assert.equal(registry.has("greet"), true, "no separate registration step");
    });

    test("scheduling a definition registers it", async () => {
        const { registry, worker } = harness();
        await worker.addSchedule({ name: "s", expr: "on 03:00", job: job("sweep", () => { }) });

        assert.equal(registry.has("sweep"), true);
    });

    test("a job can carry its own attempt limit", async () => {
        const { store, worker } = harness();
        await worker.enqueue(job("careful", () => { }, { maxAttempts: 9 }));

        assert.equal(store.all()[0].maxAttempts, 9);
    });

    test("an enqueue option beats the job's own limit", async () => {
        const { store, worker } = harness();
        await worker.enqueue(job("careful", () => { }, { maxAttempts: 9 }), undefined, { maxAttempts: 2 });

        assert.equal(store.all()[0].maxAttempts, 2);
    });

    test("registering the same name twice is refused", () => {
        const { registry } = harness();
        registry.register(job("dup", () => { }));

        assert.throws(() => registry.register(job("dup", () => { })), /already registered/);
    });

    test("jobs and schedules can be given to the constructor", async () => {
        const clock = new FakeClock(T0);
        const store = new InMemoryJobStore();
        const fired = [];
        const sweep = job("sweep", () => { fired.push(clock.nowMs()); });

        const worker = await createScheduler({
            store, clock, retry: NO_JITTER,
            jobs: [sweep],
            schedules: [{ name: "half-minute", expr: "on second=*/30", job: sweep }]
        });

        clock.advance(30_000);
        assert.equal((await worker.tick()).succeeded, 1);
        assert.deepEqual(fired, [T0 + 30_000]);
    });

    test("createScheduler applies the schema when the store has one", async () => {
        let applied = 0;
        const store = new InMemoryJobStore();
        store.ensureSchema = async () => { applied += 1; };

        const worker = await createScheduler({ store, jobs: [job("noop", () => { })] });

        assert.equal(applied, 1);
        assert.ok(worker instanceof Worker);
    });

    test("createScheduler is fine with a store that has no schema", async () => {
        const worker = await createScheduler({ store: new InMemoryJobStore() });
        assert.ok(worker instanceof Worker);
    });
});

describe("payload handling", () => {
    test("parse runs on dequeue and its result reaches the handler", async () => {
        const { worker } = harness();
        const seen = [];

        await worker.enqueue(defineJob({
            name: "typed",
            parse: raw => ({ realmId: String(raw.realmId).toUpperCase() }),
            handler: payload => { seen.push(payload); }
        }), { realmId: "tide" });

        await worker.tick();
        assert.deepEqual(seen, [{ realmId: "TIDE" }]);
    });

    test("a payload that fails parse is dead lettered without burning attempts", async () => {
        const { store, worker } = harness();

        await worker.enqueue(defineJob({
            name: "strict",
            parse: raw => {
                if (typeof raw?.realmId !== "string") throw new Error("realmId must be a string");
                return raw;
            },
            handler: () => { }
        }), { realmId: 42 }, { maxAttempts: 5 });

        assert.equal((await worker.tick()).dead, 1, "retrying cannot change a stored payload");

        const [run] = store.byStatus(JobStatus.Dead);
        assert.equal(run.attempt, 1);
        assert.match(run.lastError, /realmId must be a string/);
    });

    // The in memory store deliberately round trips payloads through JSON so a
    // job cannot pass in tests and then meet a different shape in production.
    test("payloads are normalised the way a durable store would", async () => {
        const { worker } = harness();
        const seen = [];

        class Realm {
            constructor(id) { this.id = id; }
        }

        await worker.enqueue(job("shapes", payload => { seen.push(payload); }), {
            when: new Date("2026-08-17T00:00:00Z"),
            realm: new Realm("tide"),
            missing: undefined
        });

        await worker.tick();
        assert.deepEqual(seen, [{
            when: "2026-08-17T00:00:00.000Z",
            realm: { id: "tide" }
        }]);
    });

    test("a job with no payload receives null", async () => {
        const { worker } = harness();
        const seen = [];

        await worker.enqueue(job("bare", payload => { seen.push(payload); }));
        await worker.tick();

        assert.deepEqual(seen, [null]);
    });
});

describe("worker: failure handling", () => {
    test("retries with backoff then succeeds", async () => {
        const { clock, store, worker } = harness();
        let attempts = 0;
        const flaky = job("flaky", () => {
            attempts += 1;
            if (attempts < 3) throw new Error(`boom ${attempts}`);
        });

        await worker.enqueue(flaky);

        assert.equal((await worker.tick()).retried, 1);
        assert.equal(store.all()[0].runAtMs, T0 + 1_000, "first retry waits one base delay");

        clock.advance(1_000);
        assert.equal((await worker.tick()).retried, 1);
        assert.equal(store.all()[0].runAtMs, T0 + 1_000 + 2_000, "second retry doubles");

        clock.advance(2_000);
        assert.equal((await worker.tick()).succeeded, 1);
        assert.equal(attempts, 3);
        assert.equal(store.countByStatus(JobStatus.Succeeded), 1);
    });

    test("dead letters once attempts run out", async () => {
        const { clock, store, worker } = harness();
        const doomed = job("doomed", () => { throw new Error("always fails"); });

        await worker.enqueue(doomed, undefined, { maxAttempts: 2 });

        assert.equal((await worker.tick()).retried, 1);
        clock.advance(1_000);
        assert.equal((await worker.tick()).dead, 1);

        const [run] = store.byStatus(JobStatus.Dead);
        assert.equal(run.attempt, 2);
        assert.equal(run.lastError, "always fails");
    });

    test("a permanent error skips the remaining attempts", async () => {
        const { store, worker } = harness();
        const badPayload = job("bad-payload", () => {
            throw new PermanentJobError("payload is malformed");
        });

        await worker.enqueue(badPayload, undefined, { maxAttempts: 5 });
        assert.equal((await worker.tick()).dead, 1);

        const [run] = store.byStatus(JobStatus.Dead);
        assert.equal(run.attempt, 1, "should not have burned the other attempts");
    });

    test("failures are reported to onError", async () => {
        const seen = [];
        const { worker } = harness({ onError: (err, run) => seen.push([err.message, run?.id]) });
        const noisy = job("noisy", () => { throw new Error("kaboom"); });

        await worker.enqueue(noisy);
        await worker.tick();

        assert.deepEqual(seen, [["kaboom", "run-1"]]);
    });
});

describe("worker: recurring schedules", () => {
    test("a calendar schedule materializes each occurrence once", async () => {
        const { clock, worker } = harness();
        const fired = [];
        const sweep = job("sweep", () => { fired.push(clock.nowMs()); });

        await worker.addSchedule({ name: "sweep-every-30s", expr: "on second=*/30", job: sweep });

        assert.equal((await worker.tick()).materialized, 0, "nothing is due yet");

        clock.advance(30_000);
        let result = await worker.tick();
        assert.equal(result.materialized, 1);
        assert.equal(result.succeeded, 1);

        clock.advance(30_000);
        result = await worker.tick();
        assert.equal(result.materialized, 1);
        assert.equal(result.succeeded, 1);

        assert.deepEqual(fired, [T0 + 30_000, T0 + 60_000]);
    });

    test("a fixed delay schedule chains when the run settles", async () => {
        const { clock, store, worker } = harness();
        let runs = 0;
        const sync = job("sync", () => { runs += 1; });

        await worker.addSchedule({ name: "sync-loop", expr: "every 10s", job: sync });

        clock.advance(10_000);
        assert.equal((await worker.tick()).succeeded, 1);
        assert.equal(store.countByStatus(JobStatus.Pending), 1, "successor was chained on settle");

        clock.advance(10_000);
        assert.equal((await worker.tick()).succeeded, 1);
        assert.equal(runs, 2);
    });

    test("a schedule survives a run that dead letters", async () => {
        const { clock, store, worker } = harness();
        const brittle = job("brittle", () => { throw new PermanentJobError("nope"); });

        await worker.addSchedule({
            name: "brittle-loop", expr: "every 10s", job: brittle, maxAttempts: 1
        });

        clock.advance(10_000);
        assert.equal((await worker.tick()).dead, 1);
        assert.equal(store.countByStatus(JobStatus.Pending), 1, "chain must outlive a dead run");
    });

    test("missed occurrences follow the misfire policy", async () => {
        async function missed(misfire) {
            const { clock, worker } = harness();
            const catchUp = job("catch-up", () => { });
            await worker.addSchedule({ name: "every-10s", expr: "on second=*/10", job: catchUp, misfire });

            clock.advance(35_000);
            return (await worker.tick()).materialized;
        }

        assert.equal(await missed(MisfirePolicy.FireAll), 3, "10s, 20s and 30s");
        assert.equal(await missed(MisfirePolicy.FireOnce), 1, "only the most recent");
        assert.equal(await missed(MisfirePolicy.Skip), 0, "abandon what was missed");
    });

    test("two workers sharing a store materialize an occurrence only once", async () => {
        const clock = new FakeClock(T0);
        const store = new InMemoryJobStore();
        const shared = job("shared", () => { });

        const options = { store, jobs: [shared], clock, retry: NO_JITTER, random: () => 0.5 };
        const a = new Worker({ ...options, owner: "a" });
        const b = new Worker({ ...options, owner: "b" });

        const definition = { name: "shared-sweep", expr: "on second=*/30", job: shared };
        await a.addSchedule(definition);
        await b.addSchedule(definition);

        clock.advance(30_000);
        const first = await a.tick();
        const second = await b.tick();

        assert.equal(first.materialized, 1);
        assert.equal(second.materialized, 0, "the idempotency key discarded the duplicate");
        assert.equal(store.all().length, 1);
    });
});

describe("durable schedules", () => {
    test("re-registering keeps a schedule paused", async () => {
        const scheduleStore = new InMemoryScheduleStore();
        const definition = { name: "nightly", expr: "on 03:00", job: job("sweep", () => { }) };

        const first = harness({ scheduleStore });
        await first.worker.addSchedule(definition);
        assert.equal(await first.worker.pauseSchedule("nightly"), true);

        // Standing in for a redeploy: a fresh worker over the same store.
        const second = harness({ scheduleStore });
        await second.worker.addSchedule(definition);

        const record = await second.worker.getSchedule("nightly");
        assert.equal(record.enabled, false, "a redeploy must not silently resume it");
    });

    test("re-registering keeps its place in time", async () => {
        const { clock, worker } = harness();
        const definition = { name: "nightly", expr: "on 03:00", job: job("sweep", () => { }) };

        await worker.addSchedule(definition);
        const before = (await worker.getSchedule("nightly")).nextFireAtMs;

        clock.advance(60_000);
        await worker.addSchedule(definition);

        assert.equal((await worker.getSchedule("nightly")).nextFireAtMs, before);
    });

    test("changing the expression moves the next fire time", async () => {
        const { worker } = harness();
        const sweep = job("sweep", () => { });

        await worker.addSchedule({ name: "nightly", expr: "on 03:00", job: sweep });
        const before = (await worker.getSchedule("nightly")).nextFireAtMs;

        await worker.addSchedule({ name: "nightly", expr: "on 04:00", job: sweep });
        const after = (await worker.getSchedule("nightly")).nextFireAtMs;

        assert.equal(after - before, 3_600_000);
    });

    test("a paused schedule stops materializing and resumes on demand", async () => {
        const { clock, worker } = harness();
        await worker.addSchedule({
            name: "half-minute", expr: "on second=*/30", job: job("sweep", () => { })
        });

        clock.advance(30_000);
        assert.equal((await worker.tick()).materialized, 1);

        assert.equal(await worker.pauseSchedule("half-minute"), true);
        clock.advance(30_000);
        assert.equal((await worker.tick()).materialized, 0, "paused means paused");

        assert.equal(await worker.resumeSchedule("half-minute"), true);
        clock.advance(30_000);
        assert.equal((await worker.tick()).materialized, 1);
    });

    test("pausing something that does not exist says so", async () => {
        const { worker } = harness();
        assert.equal(await worker.pauseSchedule("nope"), false);
    });

    test("removing a schedule stops it firing", async () => {
        const { clock, worker } = harness();
        await worker.addSchedule({
            name: "half-minute", expr: "on second=*/30", job: job("sweep", () => { })
        });

        assert.equal(await worker.removeSchedule("half-minute"), true);
        clock.advance(60_000);
        assert.equal((await worker.tick()).materialized, 0);
        assert.deepEqual(await worker.listSchedules(), []);
    });

    test("listing reports what is registered", async () => {
        const { worker } = harness();
        await worker.addSchedule({ name: "b", expr: "on 04:00", job: job("j2", () => { }) });
        await worker.addSchedule({ name: "a", expr: "on 03:00", job: job("j1", () => { }) });

        const listed = await worker.listSchedules();
        assert.deepEqual(listed.map(s => s.name), ["a", "b"]);
        assert.deepEqual(listed.map(s => s.expr), ["on 03:00", "on 04:00"]);
        assert.equal(listed.every(s => s.enabled), true);
    });

    test("a schedule advances its own next fire time", async () => {
        const { clock, worker } = harness();
        await worker.addSchedule({
            name: "half-minute", expr: "on second=*/30", job: job("sweep", () => { })
        });

        clock.advance(30_000);
        await worker.tick();

        const record = await worker.getSchedule("half-minute");
        assert.equal(record.lastFireAtMs, T0 + 30_000);
        assert.equal(record.nextFireAtMs, T0 + 60_000);
    });
});

describe("admin: triggering, cancelling and requeueing", () => {
    test("trigger runs a schedule now without disturbing its timetable", async () => {
        const { clock, worker } = harness();
        const fired = [];
        await worker.addSchedule({
            name: "nightly", expr: "on 03:00", job: job("sweep", () => { fired.push(clock.nowMs()); })
        });

        const before = (await worker.getSchedule("nightly")).nextFireAtMs;

        assert.notEqual(await worker.triggerSchedule("nightly"), null);
        assert.equal((await worker.tick()).succeeded, 1);

        assert.deepEqual(fired, [T0]);
        assert.equal((await worker.getSchedule("nightly")).nextFireAtMs, before,
            "a manual run must not move the schedule");
    });

    test("a paused schedule can still be triggered on purpose", async () => {
        const { worker } = harness();
        await worker.addSchedule({
            name: "nightly", expr: "on 03:00", job: job("sweep", () => { })
        });
        await worker.pauseSchedule("nightly");

        assert.notEqual(await worker.triggerSchedule("nightly"), null);
        assert.equal((await worker.tick()).succeeded, 1);
    });

    test("triggering something that does not exist says so", async () => {
        const { worker } = harness();
        assert.equal(await worker.triggerSchedule("nope"), null);
    });

    test("cancel stops a pending run", async () => {
        const { store, worker } = harness();
        let runs = 0;
        const run = await worker.enqueue(job("later", () => { runs += 1; }), undefined, {
            runAtMs: T0 + 60_000
        });

        assert.equal(await worker.cancelRun(run.id), true);
        assert.equal(store.countByStatus(JobStatus.Cancelled), 1);

        await worker.tick();
        assert.equal(runs, 0);
    });

    test("cancelling a settled run says so rather than pretending", async () => {
        const { worker } = harness();
        const run = await worker.enqueue(job("quick", () => { }));
        await worker.tick();

        assert.equal(await worker.cancelRun(run.id), false);
    });

    test("requeue puts a dead run back with a fresh set of attempts", async () => {
        const { clock, store, worker } = harness();
        let attempts = 0;
        const flaky = job("flaky", () => {
            attempts += 1;
            if (attempts === 1) throw new PermanentJobError("nope");
        });

        const run = await worker.enqueue(flaky, undefined, { maxAttempts: 1 });
        assert.equal((await worker.tick()).dead, 1);

        clock.advance(1_000);
        assert.equal(await worker.requeueRun(run.id), true);

        const requeued = await store.get(run.id);
        assert.equal(requeued.status, JobStatus.Pending);
        assert.equal(requeued.attempt, 0, "attempts start over");
        assert.equal(requeued.lastError, null);

        assert.equal((await worker.tick()).succeeded, 1);
    });

    test("requeue can also revive a cancelled run", async () => {
        const { worker, store } = harness();
        const run = await worker.enqueue(job("later", () => { }), undefined, { runAtMs: T0 + 60_000 });

        await worker.cancelRun(run.id);
        assert.equal(await worker.requeueRun(run.id), true);
        assert.equal((await store.get(run.id)).status, JobStatus.Pending);
    });

    test("requeueing a run that is not settled says so", async () => {
        const { worker } = harness();
        const run = await worker.enqueue(job("later", () => { }), undefined, { runAtMs: T0 + 60_000 });

        assert.equal(await worker.requeueRun(run.id), false);
    });

    test("cancelled runs are counted separately", async () => {
        const { worker } = harness();
        const run = await worker.enqueue(job("later", () => { }), undefined, { runAtMs: T0 + 60_000 });
        await worker.cancelRun(run.id);

        const stats = await worker.stats();
        assert.equal(stats.cancelled, 1);
        assert.equal(stats.pending, 0);
    });
});

describe("retention", () => {
    test("purges settled runs past the cutoff and leaves the rest", async () => {
        const store = new InMemoryJobStore();

        const old = await store.enqueue({ handler: "h", runAtMs: T0 });
        const recent = await store.enqueue({ handler: "h", runAtMs: T0 });
        const pending = await store.enqueue({ handler: "h", runAtMs: T0 });

        await store.complete(old.id, null, T0);
        await store.complete(recent.id, null, T0 + 10_000);

        assert.equal(await store.purgeSettled(T0 + 5_000, 100), 1);
        assert.equal(await store.get(old.id), null);
        assert.notEqual(await store.get(recent.id), null);
        assert.notEqual(await store.get(pending.id), null);
    });

    test("dead runs are kept unless asked for", async () => {
        const store = new InMemoryJobStore();
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.deadLetter(run.id, "gave up", null, T0);

        assert.equal(await store.purgeSettled(T0 + 5_000, 100), 0);
        assert.equal(await store.purgeSettled(T0 + 5_000, 100, true), 1);
    });

    test("the batch limit bounds a single sweep", async () => {
        const store = new InMemoryJobStore();
        for (let i = 0; i < 5; i++) {
            const run = await store.enqueue({ handler: "h", runAtMs: T0 });
            await store.complete(run.id, null, T0);
        }

        assert.equal(await store.purgeSettled(T0 + 1, 2), 2);
        assert.equal(await store.purgeSettled(T0 + 1, 2), 2);
        assert.equal(await store.purgeSettled(T0 + 1, 2), 1);
        assert.equal(await store.purgeSettled(T0 + 1, 2), 0);
    });

    test("the worker sweeps on its own interval", async () => {
        const { clock, store, worker } = harness({
            retention: { afterMs: 60_000, everyMs: 30_000 }
        });
        const done = job("done", () => { });

        await worker.enqueue(done);
        await worker.tick();
        assert.equal(store.countByStatus(JobStatus.Succeeded), 1);

        // Not old enough yet, and the sweep interval has not come round either.
        clock.advance(30_000);
        assert.equal((await worker.tick()).purged, 0);

        clock.advance(61_000);
        assert.equal((await worker.tick()).purged, 1);
        assert.equal(store.all().length, 0);
    });
});

describe("stats", () => {
    test("counts by status and reports the oldest waiting run", async () => {
        const store = new InMemoryJobStore();

        await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.enqueue({ handler: "h", runAtMs: T0 + 5_000 });
        const done = await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.complete(done.id, null, T0);

        const stats = await store.stats(T0 + 10_000);
        assert.equal(stats.pending, 2);
        assert.equal(stats.succeeded, 1);
        assert.equal(stats.dead, 0);
        assert.equal(stats.oldestPendingAgeMs, 10_000);
    });

    test("a run that is not due yet does not count as waiting", async () => {
        const store = new InMemoryJobStore();
        await store.enqueue({ handler: "h", runAtMs: T0 + 60_000 });

        const stats = await store.stats(T0);
        assert.equal(stats.pending, 1);
        assert.equal(stats.oldestPendingAgeMs, 0);
    });
});

// These run on the real clock because they are about elapsed time relative to a
// lease. Timings are kept small and the margins are wide.
describe("automatic lease renewal", () => {
    const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

    function slowHandlerSetup() {
        const store = new InMemoryJobStore();

        const state = { finished: false, stolen: 0 };
        const busy = job("slow", async () => {
            await wait(600);
            state.finished = true;
        });
        const thief = job("slow", () => { state.stolen += 1; });

        return { store, busy, thief, state };
    }

    test("start keeps the lease alive for as long as the handler runs", async () => {
        const { store, busy, thief, state } = slowHandlerSetup();

        const owner = new Worker({
            store, jobs: [busy], owner: "owner",
            leaseMs: 200, heartbeatMs: 50, pollIntervalMs: 20
        });
        const other = new Worker({ store, jobs: [thief], owner: "other", leaseMs: 200 });

        await owner.enqueue(busy);
        owner.start();

        // Well past the lease, so without renewal this would already be stealable.
        await wait(350);
        await other.tick();

        await wait(500);
        await owner.stop();

        assert.equal(state.finished, true);
        assert.equal(state.stolen, 0, "the lease was renewed, so nobody could take it");
    });

    test("a bare tick does not renew, and the lease lapses", async () => {
        const { store, busy, thief, state } = slowHandlerSetup();

        const owner = new Worker({ store, jobs: [busy], owner: "owner", leaseMs: 200 });
        const other = new Worker({ store, jobs: [thief], owner: "other", leaseMs: 200 });

        await owner.enqueue(busy);

        // Deliberately not awaited: tick is blocked on the handler, which is
        // exactly why renewal cannot live inside it.
        const ticking = owner.tick();

        await wait(350);
        const result = await other.tick();

        assert.equal(result.reaped, 1, "the lease expired while the handler was still working");
        assert.equal(state.stolen, 1, "and another worker picked the run up");

        await ticking;
    });
});

describe("notifiers", () => {
    const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

    test("wait resolves as soon as it is notified", async () => {
        const notifier = new InMemoryNotifier();
        const started = Date.now();

        const waiting = notifier.wait(5_000);
        await notifier.notify();
        await waiting;

        assert.ok(Date.now() - started < 1_000, "should not have waited out the timeout");
    });

    test("wait gives up after the timeout when nothing happens", async () => {
        const started = Date.now();
        await new InMemoryNotifier().wait(50);

        assert.ok(Date.now() - started >= 45, "should have waited");
    });

    test("wait returns when the signal aborts", async () => {
        const controller = new AbortController();
        const waiting = new InMemoryNotifier().wait(5_000, controller.signal);

        controller.abort();
        await waiting;
    });

    test("an already aborted signal does not wait at all", async () => {
        const controller = new AbortController();
        controller.abort();

        const started = Date.now();
        await new InMemoryNotifier().wait(5_000, controller.signal);

        assert.ok(Date.now() - started < 1_000);
    });

    test("notifying with nobody waiting is harmless", async () => {
        await new InMemoryNotifier().notify();
    });

    test("every waiter is woken", async () => {
        const notifier = new InMemoryNotifier();
        const waiters = [notifier.wait(5_000), notifier.wait(5_000), notifier.wait(5_000)];

        await notifier.notify();
        await Promise.all(waiters);
    });

    // Real clock: the point of a notifier is elapsed time, so there is nothing
    // to assert on a fake one. Kept short, with a wide margin.
    test("a running worker picks up work without waiting out the poll interval", async () => {
        const store = new InMemoryJobStore();
        const notifier = new InMemoryNotifier();
        const ran = { at: null };

        const quick = job("quick", () => { ran.at = Date.now(); });
        const options = { store, notifier, jobs: [quick], pollIntervalMs: 5_000, retry: NO_JITTER };

        const consumer = new Worker({ ...options, owner: "consumer" });
        const producer = new Worker({ ...options, owner: "producer" });

        consumer.start();
        await wait(100);

        const enqueued = Date.now();
        await producer.enqueue(quick);
        await wait(300);
        await consumer.stop();

        assert.notEqual(ran.at, null, "the job should have run");
        assert.ok(ran.at - enqueued < 1_000, `woken in ${ran.at - enqueued}ms, not after 5s`);
    });

    test("without a notifier the same worker is still waiting", async () => {
        const store = new InMemoryJobStore();
        const ran = { at: null };

        const quick = job("quick", () => { ran.at = Date.now(); });
        const options = { store, jobs: [quick], pollIntervalMs: 5_000, retry: NO_JITTER };

        const consumer = new Worker({ ...options, owner: "consumer" });
        const producer = new Worker({ ...options, owner: "producer" });

        consumer.start();
        await wait(100);

        await producer.enqueue(quick);
        await wait(300);
        await consumer.stop();

        assert.equal(ran.at, null, "polling alone should not have got to it yet");
    });

    test("a notifier that throws costs latency, not correctness", async () => {
        const store = new InMemoryJobStore();
        const errors = [];
        const broken = {
            notify: async () => { throw new Error("notify is down"); },
            wait: async () => { throw new Error("wait is down"); }
        };

        const worker = new Worker({
            store, notifier: broken, pollIntervalMs: 50, retry: NO_JITTER,
            onError: err => errors.push(err.message)
        });

        let runs = 0;
        await worker.enqueue(job("quick", () => { runs += 1; }));

        worker.start();
        await wait(250);
        await worker.stop();

        assert.equal(runs, 1, "the job still ran");
        assert.ok(errors.length > 0, "and the failures were reported");
    });
});

describe("worker: lifecycle", () => {
    test("start and stop drive the loop without leaking it", async () => {
        const { worker } = harness({ pollIntervalMs: 1_000 });
        let ran;
        const reached = new Promise(resolve => { ran = resolve; });
        const ticker = job("tick", () => { ran(); });

        await worker.enqueue(ticker);
        worker.start();

        // Wait for the loop to actually reach the handler rather than assuming it
        // got there before the stop, which is a scheduling race.
        await reached;
        await worker.stop();
    });
});
