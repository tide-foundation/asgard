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
const { parseSchedule, nextFire, ScheduleParseError } = require(
    path.join(__dirname, "..", "..", "dist", "cjs", "index.js"));

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

// 3. Rerun a function on a schedule.
//
// In-process only. Nothing survives a restart and two replicas will both fire,
// so use this for local timers rather than for work that must happen once.

async function runOnSchedule(expr, work, signal) {
    const spec = parseSchedule(expr);

    while (!signal.aborted) {
        const next = nextFire(spec, Date.now());
        if (next === null) return;

        await new Promise(resolve => setTimeout(resolve, Math.max(0, next - Date.now())));
        if (signal.aborted) return;
        await work();
    }
}

console.log("\n--- driving a function every second, three times ---");

const controller = new AbortController();
let ticks = 0;

runOnSchedule("every 1s", async () => {
    console.log(`  tick ${++ticks} at ${new Date().toISOString()}`);
    if (ticks === 3) controller.abort();
}, controller.signal).then(() => console.log("stopped"));
