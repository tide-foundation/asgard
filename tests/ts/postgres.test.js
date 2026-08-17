// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Integration tests for the Postgres store. They need a real database because
// the properties that matter, SKIP LOCKED and single statement atomicity, only
// exist in Postgres and cannot be faked.
//
//   SCHEDULER_TEST_DATABASE_URL=postgres://user:pass@host:port/db npm run test:pg
//
// Without that variable everything except the schema drift check is skipped, so
// the default suite stays runnable with no database.

const { test, describe, before, after, beforeEach } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const {
    PostgresJobStore, SCHEDULER_SCHEMA_SQL, Worker, FakeClock, JobStatus, JitterMode, defineJob
} = require(path.join(__dirname, "..", "..", "dist", "cjs", "scheduler", "index.js"));

const DATABASE_URL = process.env.SCHEDULER_TEST_DATABASE_URL;
const T0 = Date.parse("2026-08-17T00:00:00Z");

// Comments are allowed to differ, DDL is not.
function normalizeSql(sql) {
    return sql
        .split("\n")
        .map(line => line.replace(/--.*$/, "").trimEnd())
        .filter(line => line.trim().length > 0)
        .join("\n");
}

describe("schema stays in step with sql/scheduler-schema.sql", () => {
    test("the embedded copy matches the canonical file", () => {
        const canonical = fs.readFileSync(
            path.join(__dirname, "..", "..", "sql", "scheduler-schema.sql"), "utf8");

        assert.equal(normalizeSql(SCHEDULER_SCHEMA_SQL), normalizeSql(canonical));
    });
});

describe("postgres store", { skip: DATABASE_URL ? false : "SCHEDULER_TEST_DATABASE_URL is not set" }, () => {
    let pool;
    let store;

    before(async () => {
        const { Pool } = require("pg");
        pool = new Pool({ connectionString: DATABASE_URL });
        store = new PostgresJobStore(pool);
        await store.ensureSchema();
    });

    after(async () => {
        await pool.end();
    });

    beforeEach(async () => {
        await pool.query("truncate table asgard_job_runs restart identity");
    });

    test("ensureSchema can run again over an existing schema", async () => {
        await store.ensureSchema();
        await store.ensureSchema();
    });

    test("enqueue then claim leases the run and counts the attempt", async () => {
        await store.enqueue({ handler: "h", runAtMs: T0, maxAttempts: 3 });

        const claimed = await store.claimDue("worker-a", T0, 30_000, 10);
        assert.equal(claimed.length, 1);
        assert.equal(claimed[0].status, JobStatus.Leased);
        assert.equal(claimed[0].attempt, 1);
        assert.equal(claimed[0].leaseOwner, "worker-a");
        assert.equal(claimed[0].leaseExpiresAtMs, T0 + 30_000);
        assert.equal(claimed[0].maxAttempts, 3);
    });

    test("payload round trips through jsonb", async () => {
        await store.enqueue({
            handler: "h", runAtMs: T0, payload: { realmId: "tide", retries: [1, 2], deep: { ok: true } }
        });

        const [run] = await store.claimDue("worker-a", T0, 30_000, 10);
        assert.deepEqual(run.payload, { realmId: "tide", retries: [1, 2], deep: { ok: true } });
    });

    test("a null payload stays null", async () => {
        await store.enqueue({ handler: "h", runAtMs: T0 });
        const [run] = await store.claimDue("worker-a", T0, 30_000, 10);
        assert.equal(run.payload, null);
    });

    test("runs in the future are not claimed", async () => {
        await store.enqueue({ handler: "h", runAtMs: T0 + 5_000 });

        assert.equal((await store.claimDue("worker-a", T0, 30_000, 10)).length, 0);
        assert.equal((await store.claimDue("worker-a", T0 + 5_000, 30_000, 10)).length, 1);
    });

    test("a repeated idempotency key is discarded", async () => {
        const first = await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "nightly:1" });
        const second = await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "nightly:1" });

        assert.notEqual(first, null);
        assert.equal(second, null);

        const { rows } = await pool.query("select count(*)::int as n from asgard_job_runs");
        assert.equal(rows[0].n, 1);
    });

    test("null idempotency keys never collide with each other", async () => {
        await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.enqueue({ handler: "h", runAtMs: T0 });

        const { rows } = await pool.query("select count(*)::int as n from asgard_job_runs");
        assert.equal(rows[0].n, 2);
    });

    // The property the whole design rests on. Without SKIP LOCKED these workers
    // would either block on each other or hand the same run out twice.
    test("concurrent claimers never receive the same run", async () => {
        const total = 200;
        for (let i = 0; i < total; i++) {
            await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: `job:${i}` });
        }

        const workers = 8;
        const batches = await Promise.all(
            Array.from({ length: workers }, (_, i) =>
                store.claimDue(`worker-${i}`, T0, 30_000, total)));

        const ids = batches.flat().map(run => run.id);
        assert.equal(ids.length, total, "every run should have been claimed exactly once");
        assert.equal(new Set(ids).size, total, "no run should appear in two batches");

        const { rows } = await pool.query(
            "select count(*)::int as n from asgard_job_runs where status = 'pending'");
        assert.equal(rows[0].n, 0);
    });

    test("claim respects the batch limit and takes the oldest first", async () => {
        await store.enqueue({ handler: "h", runAtMs: T0 + 2_000, idempotencyKey: "c" });
        await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "a" });
        await store.enqueue({ handler: "h", runAtMs: T0 + 1_000, idempotencyKey: "b" });

        const claimed = await store.claimDue("worker-a", T0 + 5_000, 30_000, 2);
        assert.deepEqual(claimed.map(r => r.idempotencyKey), ["a", "b"]);
    });

    test("an expired lease returns the run to pending", async () => {
        await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.claimDue("worker-a", T0, 1_000, 10);

        assert.equal(await store.reapExpired(T0 + 500), 0);
        assert.equal(await store.reapExpired(T0 + 1_001), 1);

        const [reclaimed] = await store.claimDue("worker-b", T0 + 1_001, 30_000, 10);
        assert.equal(reclaimed.attempt, 2, "the attempt already spent still counts");
        assert.equal(reclaimed.lastError, "lease expired");
    });

    test("heartbeat pushes the lease out and fails once the run is settled", async () => {
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.claimDue("worker-a", T0, 1_000, 10);

        assert.equal(await store.heartbeat(run.id, T0 + 60_000), true);
        assert.equal(await store.reapExpired(T0 + 1_001), 0);

        await store.complete(run.id, null, T0 + 2_000);
        assert.equal(await store.heartbeat(run.id, T0 + 90_000), false);
    });

    test("complete settles and chains in one statement", async () => {
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.claimDue("worker-a", T0, 30_000, 10);

        await store.complete(
            run.id,
            { handler: "h", runAtMs: T0 + 60_000, scheduleId: "loop", idempotencyKey: "loop:2" },
            T0 + 1_000);

        assert.equal((await store.get(run.id)).status, JobStatus.Succeeded);

        const [chained] = await store.claimDue("worker-a", T0 + 60_000, 30_000, 10);
        assert.equal(chained.idempotencyKey, "loop:2");
        assert.equal(chained.scheduleId, "loop");
    });

    test("dead letter settles and still chains", async () => {
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.claimDue("worker-a", T0, 30_000, 10);

        await store.deadLetter(
            run.id, "gave up", { handler: "h", runAtMs: T0 + 60_000, idempotencyKey: "loop:2" }, T0);

        const settled = await store.get(run.id);
        assert.equal(settled.status, JobStatus.Dead);
        assert.equal(settled.lastError, "gave up");

        const { rows } = await pool.query(
            "select count(*)::int as n from asgard_job_runs where status = 'pending'");
        assert.equal(rows[0].n, 1, "a dead run must not break a recurring schedule");
    });

    test("chaining a key that already exists is discarded rather than failing", async () => {
        await store.enqueue({ handler: "h", runAtMs: T0 + 60_000, idempotencyKey: "loop:2" });
        const run = await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "loop:1" });
        await store.claimDue("worker-a", T0, 30_000, 10);

        await store.complete(
            run.id, { handler: "h", runAtMs: T0 + 60_000, idempotencyKey: "loop:2" }, T0);

        const { rows } = await pool.query("select count(*)::int as n from asgard_job_runs");
        assert.equal(rows[0].n, 2);
    });

    test("retry moves the run forward and keeps the error", async () => {
        const run = await store.enqueue({ handler: "h", runAtMs: T0, maxAttempts: 3 });
        await store.claimDue("worker-a", T0, 30_000, 10);

        await store.retry(run.id, "upstream down", T0 + 5_000, T0);

        const pending = await store.get(run.id);
        assert.equal(pending.status, JobStatus.Pending);
        assert.equal(pending.runAtMs, T0 + 5_000);
        assert.equal(pending.lastError, "upstream down");
        assert.equal(pending.leaseOwner, null);
    });

    test("get returns null for a run that does not exist", async () => {
        assert.equal(await store.get("999999"), null);
    });

    test("claiming can be limited to a set of handlers", async () => {
        await store.enqueue({ handler: "known", runAtMs: T0, idempotencyKey: "a" });
        await store.enqueue({ handler: "other", runAtMs: T0, idempotencyKey: "b" });

        const claimed = await store.claimDue("worker-a", T0, 30_000, 10, ["known"]);
        assert.deepEqual(claimed.map(r => r.handler), ["known"]);

        const { rows } = await pool.query(
            "select count(*)::int as n from asgard_job_runs where status = 'pending'");
        assert.equal(rows[0].n, 1, "the other handler's run is left for someone else");
    });

    test("an empty handler list claims nothing rather than everything", async () => {
        await store.enqueue({ handler: "known", runAtMs: T0 });
        assert.equal((await store.claimDue("worker-a", T0, 30_000, 10, [])).length, 0);
    });

    test("omitting the handler list claims everything", async () => {
        await store.enqueue({ handler: "known", runAtMs: T0, idempotencyKey: "a" });
        await store.enqueue({ handler: "other", runAtMs: T0, idempotencyKey: "b" });

        assert.equal((await store.claimDue("worker-a", T0, 30_000, 10)).length, 2);
    });

    test("purge deletes settled runs past the cutoff and keeps the rest", async () => {
        const old = await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "old" });
        const recent = await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "recent" });
        const pending = await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "pending" });

        await store.complete(old.id, null, T0);
        await store.complete(recent.id, null, T0 + 10_000);

        assert.equal(await store.purgeSettled(T0 + 5_000, 100), 1);
        assert.equal(await store.get(old.id), null);
        assert.notEqual(await store.get(recent.id), null);
        assert.notEqual(await store.get(pending.id), null);
    });

    test("purge keeps dead runs unless asked for", async () => {
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.deadLetter(run.id, "gave up", null, T0);

        assert.equal(await store.purgeSettled(T0 + 5_000, 100), 0);
        assert.equal(await store.purgeSettled(T0 + 5_000, 100, true), 1);
    });

    test("purge honours the batch limit", async () => {
        for (let i = 0; i < 5; i++) {
            const run = await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: `k${i}` });
            await store.complete(run.id, null, T0);
        }

        assert.equal(await store.purgeSettled(T0 + 1, 2), 2);
        assert.equal(await store.purgeSettled(T0 + 1, 2), 2);
        assert.equal(await store.purgeSettled(T0 + 1, 2), 1);
        assert.equal(await store.purgeSettled(T0 + 1, 2), 0);
    });

    test("stats counts by status and reports the oldest waiting run", async () => {
        await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "a" });
        await store.enqueue({ handler: "h", runAtMs: T0 + 5_000, idempotencyKey: "b" });
        const done = await store.enqueue({ handler: "h", runAtMs: T0, idempotencyKey: "c" });
        await store.complete(done.id, null, T0);

        const stats = await store.stats(T0 + 10_000);
        assert.equal(stats.pending, 2);
        assert.equal(stats.succeeded, 1);
        assert.equal(stats.dead, 0);
        assert.equal(stats.oldestPendingAgeMs, 10_000);
    });

    test("stats does not count a run that is not due yet as waiting", async () => {
        await store.enqueue({ handler: "h", runAtMs: T0 + 60_000 });

        const stats = await store.stats(T0);
        assert.equal(stats.pending, 1);
        assert.equal(stats.oldestPendingAgeMs, 0);
    });

    test("stats on an empty table reports zeros", async () => {
        const stats = await store.stats(T0);
        assert.deepEqual(stats, {
            pending: 0, leased: 0, succeeded: 0, dead: 0, oldestPendingAgeMs: 0
        });
    });
});

describe("worker on postgres", { skip: DATABASE_URL ? false : "SCHEDULER_TEST_DATABASE_URL is not set" }, () => {
    let pool;
    let store;

    before(async () => {
        const { Pool } = require("pg");
        pool = new Pool({ connectionString: DATABASE_URL });
        store = new PostgresJobStore(pool);
        await store.ensureSchema();
    });

    after(async () => {
        await pool.end();
    });

    beforeEach(async () => {
        await pool.query("truncate table asgard_job_runs restart identity");
    });

    function newWorker(clock, jobs, owner) {
        return new Worker({
            store,
            jobs,
            clock,
            owner,
            leaseMs: 30_000,
            retry: { maxAttempts: 3, baseMs: 1_000, capMs: 60_000, multiplier: 2, jitter: JitterMode.None },
            random: () => 0.5
        });
    }

    test("runs a job end to end", async () => {
        const clock = new FakeClock(T0);
        const seen = [];
        const greet = defineJob({ name: "greet", handler: payload => seen.push(payload) });

        const worker = newWorker(clock, [greet], "solo");
        await worker.enqueue(greet, { name: "asgard" });

        const result = await worker.tick();
        assert.equal(result.succeeded, 1);
        assert.deepEqual(seen, [{ name: "asgard" }]);
    });

    test("retries with backoff then succeeds", async () => {
        const clock = new FakeClock(T0);
        let attempts = 0;
        const flaky = defineJob({
            name: "flaky",
            handler: () => {
                attempts += 1;
                if (attempts < 3) throw new Error("not yet");
            }
        });

        const worker = newWorker(clock, [flaky], "solo");
        const run = await worker.enqueue(flaky);

        assert.equal((await worker.tick()).retried, 1);
        assert.equal((await store.get(run.id)).runAtMs, T0 + 1_000);

        clock.advance(1_000);
        assert.equal((await worker.tick()).retried, 1);

        clock.advance(2_000);
        assert.equal((await worker.tick()).succeeded, 1);
        assert.equal(attempts, 3);
    });

    test("two workers sharing a database run each occurrence once", async () => {
        const clock = new FakeClock(T0);
        let runs = 0;
        const sweep = defineJob({ name: "sweep", handler: () => { runs += 1; } });

        const a = newWorker(clock, [sweep], "a");
        const b = newWorker(clock, [sweep], "b");

        const definition = { name: "shared-sweep", expr: "on second=*/30", job: sweep };
        a.addSchedule(definition);
        b.addSchedule(definition);

        clock.advance(30_000);
        const [first, second] = await Promise.all([a.tick(), b.tick()]);

        assert.equal(first.materialized + second.materialized, 1, "materialized once");
        assert.equal(first.claimed + second.claimed, 1, "claimed once");
        assert.equal(runs, 1, "and executed once");
    });
});
