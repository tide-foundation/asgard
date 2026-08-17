// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Requires: npm run build:cjs

const { test, describe } = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const {
    FakeClock, InMemoryJobStore, HandlerRegistry, Worker, MisfirePolicy,
    JobStatus, JitterMode, retryDelayMs, shouldRetry, PermanentJobError
} = require(path.join(__dirname, "..", "..", "dist", "cjs", "scheduler", "index.js"));

const T0 = Date.parse("2026-08-17T00:00:00Z");

// Fixed backoff so a run lands on a predictable instant.
const NO_JITTER = {
    maxAttempts: 3, baseMs: 1_000, capMs: 60_000, multiplier: 2, jitter: JitterMode.None
};

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
        const { store, registry, worker } = harness();
        const seen = [];
        registry.register("greet", async (payload, ctx) => {
            seen.push({ payload, attempt: ctx.attempt });
        });

        await worker.enqueue("greet", { name: "asgard" });
        const result = await worker.tick();

        assert.equal(result.claimed, 1);
        assert.equal(result.succeeded, 1);
        assert.deepEqual(seen, [{ payload: { name: "asgard" }, attempt: 1 }]);
        assert.equal(store.countByStatus(JobStatus.Succeeded), 1);
    });

    test("a job scheduled for later is not run yet", async () => {
        const { clock, worker, registry } = harness();
        let runs = 0;
        registry.register("later", () => { runs += 1; });

        await worker.enqueue("later", null, { runAtMs: T0 + 10_000 });
        assert.equal((await worker.tick()).claimed, 0);

        clock.advance(10_000);
        assert.equal((await worker.tick()).succeeded, 1);
        assert.equal(runs, 1);
    });

    test("an unknown handler goes straight to dead", async () => {
        const { store, worker } = harness();
        await worker.enqueue("missing");

        const result = await worker.tick();
        assert.equal(result.dead, 1);

        const [run] = store.byStatus(JobStatus.Dead);
        assert.match(run.lastError, /no handler registered/);
    });
});

describe("worker: failure handling", () => {
    test("retries with backoff then succeeds", async () => {
        const { clock, store, registry, worker } = harness();
        let attempts = 0;
        registry.register("flaky", () => {
            attempts += 1;
            if (attempts < 3) throw new Error(`boom ${attempts}`);
        });

        await worker.enqueue("flaky");

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
        const { clock, store, registry, worker } = harness();
        registry.register("doomed", () => { throw new Error("always fails"); });

        await worker.enqueue("doomed", null, { maxAttempts: 2 });

        assert.equal((await worker.tick()).retried, 1);
        clock.advance(1_000);
        assert.equal((await worker.tick()).dead, 1);

        const [run] = store.byStatus(JobStatus.Dead);
        assert.equal(run.attempt, 2);
        assert.equal(run.lastError, "always fails");
    });

    test("a permanent error skips the remaining attempts", async () => {
        const { store, registry, worker } = harness();
        registry.register("bad-payload", () => {
            throw new PermanentJobError("payload is malformed");
        });

        await worker.enqueue("bad-payload", null, { maxAttempts: 5 });
        assert.equal((await worker.tick()).dead, 1);

        const [run] = store.byStatus(JobStatus.Dead);
        assert.equal(run.attempt, 1, "should not have burned the other attempts");
    });

    test("failures are reported to onError", async () => {
        const seen = [];
        const { registry, worker } = harness({ onError: (err, run) => seen.push([err.message, run?.id]) });
        registry.register("noisy", () => { throw new Error("kaboom"); });

        await worker.enqueue("noisy");
        await worker.tick();

        assert.deepEqual(seen, [["kaboom", "run-1"]]);
    });
});

describe("worker: recurring schedules", () => {
    test("a calendar schedule materializes each occurrence once", async () => {
        const { clock, registry, worker } = harness();
        const fired = [];
        registry.register("sweep", () => { fired.push(clock.nowMs()); });

        worker.addSchedule({ name: "sweep-every-30s", expr: "on second=*/30", handler: "sweep" });

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
        const { clock, store, registry, worker } = harness();
        let runs = 0;
        registry.register("sync", () => { runs += 1; });

        worker.addSchedule({ name: "sync-loop", expr: "every 10s", handler: "sync" });

        clock.advance(10_000);
        assert.equal((await worker.tick()).succeeded, 1);
        assert.equal(store.countByStatus(JobStatus.Pending), 1, "successor was chained on settle");

        clock.advance(10_000);
        assert.equal((await worker.tick()).succeeded, 1);
        assert.equal(runs, 2);
    });

    test("a schedule survives a run that dead letters", async () => {
        const { clock, store, registry, worker } = harness();
        registry.register("brittle", () => { throw new PermanentJobError("nope"); });

        worker.addSchedule({
            name: "brittle-loop", expr: "every 10s", handler: "brittle", maxAttempts: 1
        });

        clock.advance(10_000);
        assert.equal((await worker.tick()).dead, 1);
        assert.equal(store.countByStatus(JobStatus.Pending), 1, "chain must outlive a dead run");
    });

    test("missed occurrences follow the misfire policy", async () => {
        async function missed(misfire) {
            const { clock, registry, worker } = harness();
            registry.register("catch-up", () => { });
            worker.addSchedule({ name: "every-10s", expr: "on second=*/10", handler: "catch-up", misfire });

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
        const registry = new HandlerRegistry();
        registry.register("shared", () => { });

        const options = { store, registry, clock, retry: NO_JITTER, random: () => 0.5 };
        const a = new Worker({ ...options, owner: "a" });
        const b = new Worker({ ...options, owner: "b" });

        const definition = { name: "shared-sweep", expr: "on second=*/30", handler: "shared" };
        a.addSchedule(definition);
        b.addSchedule(definition);

        clock.advance(30_000);
        const first = await a.tick();
        const second = await b.tick();

        assert.equal(first.materialized, 1);
        assert.equal(second.materialized, 0, "the idempotency key discarded the duplicate");
        assert.equal(store.all().length, 1);
    });
});

describe("worker: lifecycle", () => {
    test("start and stop drive the loop without leaking it", async () => {
        const { registry, worker } = harness({ pollIntervalMs: 1_000 });
        let ran;
        const reached = new Promise(resolve => { ran = resolve; });
        registry.register("tick", () => { ran(); });

        await worker.enqueue("tick");
        worker.start();

        // Wait for the loop to actually reach the handler rather than assuming it
        // got there before the stop, which is a scheduling race.
        await reached;
        await worker.stop();
    });
});
