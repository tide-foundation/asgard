// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Runnable tour of the scheduler expression API.
//
//   npm run build:cjs
//   node examples/typescript/scheduler.js
//
// In TypeScript the import is:
//   import { parseSchedule, nextFire, ScheduleParseError } from "asgard-tide";

const path = require("node:path");
const {
    parseSchedule, nextFire, ScheduleParseError,
    HandlerRegistry, InMemoryJobStore, Worker, PermanentJobError, JobStatus
} = require(path.join(__dirname, "..", "..", "dist", "cjs", "index.js"));

// 1. Preview when a schedule will fire.
//
// Parse once and keep the spec. It is immutable, so the same one can drive any
// number of evaluations.

function preview(expr, count = 3, from = Date.now()) {
    const spec = parseSchedule(expr);
    const fires = [];
    let cursor = from;

    for (let i = 0; i < count; i++) {
        cursor = nextFire(spec, cursor);
        if (cursor === null) { fires.push("never again"); break; }
        fires.push(new Date(cursor).toISOString());
    }

    console.log(expr.padEnd(42), fires.join("  "));
}

console.log("--- upcoming fires ---");
preview("on 03:00");
preview("on 09:30 dow=mon,wed,fri");
preview("on minute=*/15");
preview("on day=last 23:55");
preview("on nth=2 dow=tue 10:00");
preview("on 02:30 tz=Australia/Sydney");
preview("every 90m");
preview("at 2030-01-01T00:00:00Z");

// Calendar edge cases resolve rather than throwing.
console.log("\n--- calendar edge cases ---");
preview("on day=29 month=2", 3);      // leap days only
preview("on day=31", 3);              // skips short months
preview("on day=30 month=2", 1);      // never fires, and says so

// 2. Bad expressions carry a code and a character offset.

console.log("\n--- error reporting ---");
for (const bad of ["on hour=25", "on day=1 dow=mon", "every 5x", "on nth=2 hour=10"]) {
    try {
        parseSchedule(bad);
    } catch (err) {
        if (!(err instanceof ScheduleParseError)) throw err;
        console.log(`  ${bad}`);
        console.log(`  ${" ".repeat(err.offset)}^ ${err.code}`);
    }
}

// 3. Actually run jobs.
//
// A worker claims due work from a store and dispatches it to handlers looked up
// by name. InMemoryJobStore keeps everything in the process, so nothing survives
// a restart. Swap in a durable JobStore and the same worker coordinates across
// replicas.

async function runWorker() {
    const store = new InMemoryJobStore();
    const registry = new HandlerRegistry();

    registry.register("heartbeat", (payload) => {
        console.log(`  heartbeat ${payload.label} at ${new Date().toISOString()}`);
    });

    // Fails twice, then succeeds. The worker backs off between attempts.
    let attempts = 0;
    registry.register("flaky", (_, ctx) => {
        attempts += 1;
        console.log(`  flaky attempt ${ctx.attempt} of ${ctx.maxAttempts}`);
        if (attempts < 3) throw new Error("upstream not ready");
    });

    // Cannot succeed no matter how many times it runs, so it skips its
    // remaining attempts and goes straight to dead.
    registry.register("malformed", () => {
        throw new PermanentJobError("payload is missing a realm id");
    });

    const worker = new Worker({
        store,
        registry,
        concurrency: 2,
        pollIntervalMs: 100,
        retry: { maxAttempts: 4, baseMs: 200, capMs: 5_000, multiplier: 2, jitter: "none" }
    });

    worker.addSchedule({
        name: "heartbeat-every-second",
        expr: "every 1s",
        handler: "heartbeat",
        payload: { label: "tide" }
    });

    await worker.enqueue("flaky");
    await worker.enqueue("malformed");

    worker.start();
    await new Promise(resolve => setTimeout(resolve, 3_000));
    await worker.stop();

    console.log("\n--- final state ---");
    for (const status of [JobStatus.Succeeded, JobStatus.Pending, JobStatus.Dead]) {
        console.log(`  ${status.padEnd(10)} ${store.countByStatus(status)}`);
    }
    for (const run of store.byStatus(JobStatus.Dead)) {
        console.log(`  dead: ${run.handler} after ${run.attempt} attempt(s), ${run.lastError}`);
    }
}

console.log("\n--- running a worker for three seconds ---");
runWorker().then(() => console.log("stopped"));
