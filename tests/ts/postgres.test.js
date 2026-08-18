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
    PostgresJobStore, PostgresScheduleStore, SCHEDULER_MIGRATIONS, migrate, appliedMigrations,
    PostgresNotifier, JOB_CHANNEL,
    Worker, FakeClock, JobStatus, JitterMode, defineJob, parseSchedule, MisfirePolicy
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

describe("migrations stay in step with sql/migrations", () => {
    const dir = path.join(__dirname, "..", "..", "sql", "migrations");

    test("every file on disk is embedded, and nothing extra is", () => {
        const onDisk = fs.readdirSync(dir).filter(f => f.endsWith(".sql")).sort();
        const embedded = SCHEDULER_MIGRATIONS.map(m => `${m.name}.sql`);

        assert.deepEqual(embedded, onDisk);
    });

    for (const migration of SCHEDULER_MIGRATIONS) {
        test(`${migration.name} matches its file`, () => {
            const canonical = fs.readFileSync(path.join(dir, `${migration.name}.sql`), "utf8");
            assert.equal(normalizeSql(migration.sql), normalizeSql(canonical));
        });
    }

    test("versions start at 1 and increase by one", () => {
        assert.deepEqual(
            SCHEDULER_MIGRATIONS.map(m => m.version),
            SCHEDULER_MIGRATIONS.map((_, i) => i + 1));
    });

    test("a file's number matches the version it declares", () => {
        for (const migration of SCHEDULER_MIGRATIONS) {
            assert.equal(Number(migration.name.split("-")[0]), migration.version, migration.name);
        }
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
        await pool.query("truncate table asgard_schedules");
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
            pending: 0, leased: 0, succeeded: 0, dead: 0, cancelled: 0, oldestPendingAgeMs: 0
        });
    });

    test("cancel stops a pending run and refuses a settled one", async () => {
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });

        assert.equal(await store.cancel(run.id, T0), true);
        assert.equal((await store.get(run.id)).status, JobStatus.Cancelled);
        assert.equal(await store.cancel(run.id, T0), false);
    });

    test("cancel works on a leased run too", async () => {
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });
        await store.claimDue("worker-a", T0, 30_000, 10);

        assert.equal(await store.cancel(run.id, T0), true);
        assert.equal((await store.get(run.id)).leaseOwner, null);
    });

    test("requeue revives a dead run with attempts reset", async () => {
        const run = await store.enqueue({ handler: "h", runAtMs: T0, maxAttempts: 3 });
        await store.claimDue("worker-a", T0, 30_000, 10);
        await store.deadLetter(run.id, "gave up", null, T0);

        assert.equal(await store.requeue(run.id, T0 + 5_000, T0), true);

        const revived = await store.get(run.id);
        assert.equal(revived.status, JobStatus.Pending);
        assert.equal(revived.attempt, 0);
        assert.equal(revived.lastError, null);
        assert.equal(revived.runAtMs, T0 + 5_000);
    });

    test("requeue refuses a run that is not settled", async () => {
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });
        assert.equal(await store.requeue(run.id, T0, T0), false);
    });
});

describe("postgres schedule store", { skip: DATABASE_URL ? false : "SCHEDULER_TEST_DATABASE_URL is not set" }, () => {
    let pool;
    let schedules;

    before(async () => {
        const { Pool } = require("pg");
        pool = new Pool({ connectionString: DATABASE_URL });
        await new PostgresJobStore(pool).ensureSchema();
        schedules = new PostgresScheduleStore(pool);
    });

    after(async () => {
        await pool.end();
    });

    beforeEach(async () => {
        await pool.query("truncate table asgard_schedules");
    });

    const upsert = (overrides = {}) => schedules.upsert({
        name: "nightly",
        handler: "sweep",
        payload: { realmId: "tide" },
        expr: "on 03:00",
        spec: parseSchedule("on 03:00"),
        misfire: MisfirePolicy.FireOnce,
        maxAttempts: 3,
        nextFireAtMs: T0 + 10_000,
        ...overrides
    }, T0);

    test("upsert inserts and reads back every field", async () => {
        const record = await upsert();

        assert.equal(record.name, "nightly");
        assert.equal(record.handler, "sweep");
        assert.deepEqual(record.payload, { realmId: "tide" });
        assert.equal(record.expr, "on 03:00");
        assert.equal(record.enabled, true);
        assert.equal(record.misfire, MisfirePolicy.FireOnce);
        assert.equal(record.maxAttempts, 3);
        assert.equal(record.nextFireAtMs, T0 + 10_000);
    });

    test("the spec survives the round trip and still evaluates", async () => {
        await schedules.upsert({
            name: "sydney", handler: "sweep", expr: "on 02:30 tz=Australia/Sydney",
            spec: parseSchedule("on 02:30 tz=Australia/Sydney"),
            misfire: MisfirePolicy.FireOnce, maxAttempts: null, nextFireAtMs: T0
        }, T0);

        const record = await schedules.get("sydney");
        assert.equal(record.spec.kind, "calendar");
        assert.equal(record.spec.tz, "Australia/Sydney");
        assert.deepEqual(record.spec.hour.values, [2]);
    });

    test("re-registering keeps enabled and the next fire time", async () => {
        await upsert();
        await schedules.setEnabled("nightly", false, T0);
        await schedules.advance("nightly", T0 + 99_000, T0, T0);

        await upsert({ payload: { realmId: "changed" } });

        const record = await schedules.get("nightly");
        assert.equal(record.enabled, false, "a redeploy must not silently resume it");
        assert.equal(record.nextFireAtMs, T0 + 99_000, "and must not move it in time");
        assert.deepEqual(record.payload, { realmId: "changed" }, "but the definition does update");
    });

    test("changing the spec does reset the next fire time", async () => {
        await upsert();
        await schedules.advance("nightly", T0 + 99_000, T0, T0);

        await upsert({ expr: "on 04:00", spec: parseSchedule("on 04:00"), nextFireAtMs: T0 + 20_000 });

        assert.equal((await schedules.get("nightly")).nextFireAtMs, T0 + 20_000);
    });

    test("listDue only returns enabled schedules that have come due", async () => {
        await upsert({ name: "due", nextFireAtMs: T0 });
        await upsert({ name: "later", nextFireAtMs: T0 + 60_000 });
        await upsert({ name: "paused", nextFireAtMs: T0 });
        await schedules.setEnabled("paused", false, T0);

        const due = await schedules.listDue(T0, 10);
        assert.deepEqual(due.map(s => s.name), ["due"]);
    });

    test("a schedule with no next fire time is never due", async () => {
        await upsert({ nextFireAtMs: null });
        assert.deepEqual(await schedules.listDue(T0 + 1_000_000, 10), []);
    });

    test("advance records where it got to", async () => {
        await upsert();
        await schedules.advance("nightly", T0 + 60_000, T0 + 10_000, T0);

        const record = await schedules.get("nightly");
        assert.equal(record.nextFireAtMs, T0 + 60_000);
        assert.equal(record.lastFireAtMs, T0 + 10_000);
    });

    test("setEnabled and remove report whether they found anything", async () => {
        await upsert();

        assert.equal(await schedules.setEnabled("nightly", false, T0), true);
        assert.equal(await schedules.setEnabled("nope", false, T0), false);
        assert.equal(await schedules.remove("nightly"), true);
        assert.equal(await schedules.remove("nightly"), false);
        assert.equal(await schedules.get("nightly"), null);
    });

    test("list is ordered by name", async () => {
        await upsert({ name: "b" });
        await upsert({ name: "a" });

        assert.deepEqual((await schedules.list()).map(s => s.name), ["a", "b"]);
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
        await pool.query("truncate table asgard_schedules");
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


describe("postgres notifier", { skip: DATABASE_URL ? false : "SCHEDULER_TEST_DATABASE_URL is not set" }, () => {
    let pool;
    let listener;
    let notifier;

    before(async () => {
        const { Client, Pool } = require("pg");
        pool = new Pool({ connectionString: DATABASE_URL });

        // LISTEN needs its own session, so this is a Client rather than a Pool.
        listener = new Client({ connectionString: DATABASE_URL });
        await listener.connect();

        notifier = new PostgresNotifier(pool, listener);
    });

    after(async () => {
        await listener.end();
        await pool.end();
    });

    test("a notify on one connection wakes a wait on another", async () => {
        const started = Date.now();
        const waiting = notifier.wait(5_000);

        // Give the LISTEN a moment to be registered before announcing.
        await new Promise(resolve => setTimeout(resolve, 100));
        await notifier.notify();
        await waiting;

        assert.ok(Date.now() - started < 2_000, "should not have waited out the timeout");
    });

    test("a notify from a completely separate connection also wakes it", async () => {
        const { Pool } = require("pg");
        const other = new Pool({ connectionString: DATABASE_URL });

        try {
            const waiting = notifier.wait(5_000);
            await new Promise(resolve => setTimeout(resolve, 100));

            // Standing in for another process entirely.
            await other.query(`select pg_notify('${JOB_CHANNEL}', '')`);

            const started = Date.now();
            await waiting;
            assert.ok(Date.now() - started < 2_000, "should have been woken");
        } finally {
            await other.end();
        }
    });

    test("noise on another channel is ignored", async () => {
        const started = Date.now();
        await notifier.wait(150);
        const idle = Date.now() - started;

        await pool.query("select pg_notify('some_other_channel', '')");
        assert.ok(idle >= 140, "an unrelated channel must not shorten the wait");
    });

    test("wait gives up after the timeout when nothing happens", async () => {
        const started = Date.now();
        await notifier.wait(150);

        assert.ok(Date.now() - started >= 140);
    });

    test("wait returns when the signal aborts", async () => {
        const controller = new AbortController();
        const waiting = notifier.wait(5_000, controller.signal);

        controller.abort();
        await waiting;
    });
});

// Last in the file on purpose: these drop and rebuild the tables, so nothing
// after them can depend on the state they leave behind.
describe("migrating a database", { skip: DATABASE_URL ? false : "SCHEDULER_TEST_DATABASE_URL is not set" }, () => {
    let pool;

    before(async () => {
        const { Pool } = require("pg");
        pool = new Pool({ connectionString: DATABASE_URL });
    });

    after(async () => {
        // Leave the database usable for a re-run.
        await migrate(pool);
        await pool.end();
    });

    const wipe = () => pool.query(
        "drop table if exists asgard_job_runs, asgard_schedules, asgard_schema_migrations cascade");

    test("a fresh database gets every migration, in order", async () => {
        await wipe();

        const applied = await migrate(pool);
        assert.deepEqual(applied, SCHEDULER_MIGRATIONS.map(m => m.version));
        assert.deepEqual(await appliedMigrations(pool), applied);
    });

    test("migrating again does nothing", async () => {
        await wipe();
        await migrate(pool);

        assert.deepEqual(await migrate(pool), [], "already up to date");
    });

    test("only the missing ones are applied", async () => {
        await wipe();
        await migrate(pool);

        // Forget the last migration, as though this database were one behind.
        const last = SCHEDULER_MIGRATIONS[SCHEDULER_MIGRATIONS.length - 1];
        await pool.query("delete from asgard_schema_migrations where version = $1", [last.version]);

        assert.deepEqual(await migrate(pool), [last.version]);
    });

    test("each migration is safe to apply twice", async () => {
        await wipe();
        await migrate(pool);

        // Replaying every migration against an already migrated database is the
        // situation a crash between applying and recording leaves behind.
        for (const migration of SCHEDULER_MIGRATIONS) {
            await pool.query(migration.sql);
        }

        assert.deepEqual(await appliedMigrations(pool), SCHEDULER_MIGRATIONS.map(m => m.version));
    });

    test("concurrent migrators do not trip over each other", async () => {
        await wipe();

        const results = await Promise.all(Array.from({ length: 5 }, () => migrate(pool)));

        // Exactly one caller does each piece of work, the rest find it done.
        const total = results.reduce((sum, versions) => sum + versions.length, 0);
        assert.equal(total, SCHEDULER_MIGRATIONS.length,
            "each migration should be applied by exactly one caller");
        assert.deepEqual(await appliedMigrations(pool), SCHEDULER_MIGRATIONS.map(m => m.version));
    });

    test("the schema works after migrating from nothing", async () => {
        await wipe();
        await migrate(pool);

        const store = new PostgresJobStore(pool);
        const run = await store.enqueue({ handler: "h", runAtMs: T0 });
        assert.equal(await store.cancel(run.id, T0), true, "the cancelled status exists");
    });
});
