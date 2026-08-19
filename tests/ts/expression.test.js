// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Unit tests for behaviour the shared fixtures do not reach, mostly the shape
// of the parsed spec rather than the instants it produces. The .NET suite in
// Tide.Asgard.Scheduler.Tests mirrors this file case for case.
//
// Requires: npm run build:cjs

const { test, describe } = require("node:test");
const assert = require("node:assert/strict");
const path = require("node:path");

const {
    parseSchedule, nextFire, parseDuration, ScheduleErrorCode
} = require(path.join(__dirname, "..", "..", "dist", "cjs", "scheduler", "index.js"));

const at = (iso) => Date.parse(iso);

function captureError(fn) {
    try {
        fn();
    } catch (err) {
        return err;
    }
    return null;
}

describe("duration literals", () => {
    test("single unit", () => {
        assert.equal(parseDuration("30s"), 30_000);
    });

    test("compound units descend in size", () => {
        assert.equal(parseDuration("1h30m"), 5_400_000);
    });

    test("milliseconds", () => {
        assert.equal(parseDuration("500ms"), 500);
    });

    test("units out of order are rejected", () => {
        const thrown = captureError(() => parseDuration("30m1h"));
        assert.equal(thrown.code, ScheduleErrorCode.BadDuration);
    });

    test("zero is rejected", () => {
        const thrown = captureError(() => parseDuration("0s"));
        assert.equal(thrown.code, ScheduleErrorCode.BadDuration);
    });
});

describe("defaulting rule", () => {
    test("naming hour zeroes minute and second and leaves the rest open", () => {
        const spec = parseSchedule("on hour=3");
        assert.deepEqual(spec.second.values, [0]);
        assert.deepEqual(spec.minute.values, [0]);
        assert.deepEqual(spec.hour.values, [3]);
        assert.equal(spec.day.isAny, true);
        assert.equal(spec.month.isAny, true);
    });

    test("naming month collapses day as well", () => {
        const spec = parseSchedule("on month=7");
        assert.deepEqual(spec.month.values, [7]);
        assert.deepEqual(spec.day.values, [1]);
        assert.deepEqual(spec.hour.values, [0]);
    });

    test("naming second leaves minute and hour open", () => {
        const spec = parseSchedule("on second=30");
        assert.deepEqual(spec.second.values, [30]);
        assert.equal(spec.minute.isAny, true);
        assert.equal(spec.hour.isAny, true);
    });

    test("dow counts as the day rung", () => {
        const spec = parseSchedule("on dow=mon");
        assert.deepEqual(spec.dow.values, [1]);
        assert.equal(spec.day.isAny, true);
        assert.deepEqual(spec.hour.values, [0]);
    });
});

describe("value syntax", () => {
    test("list", () => {
        assert.deepEqual(parseSchedule("on hour=1,5,9").hour.values, [1, 5, 9]);
    });

    test("range", () => {
        assert.deepEqual(parseSchedule("on hour=9-12").hour.values, [9, 10, 11, 12]);
    });

    test("wildcard with step", () => {
        assert.deepEqual(parseSchedule("on hour=*/6").hour.values, [0, 6, 12, 18]);
    });

    test("range with step", () => {
        assert.deepEqual(parseSchedule("on hour=9-17/4").hour.values, [9, 13, 17]);
    });

    test("bare value with step runs to the field maximum", () => {
        assert.deepEqual(parseSchedule("on minute=50/5").minute.values, [50, 55]);
    });

    test("named weekdays resolve with sunday as zero", () => {
        assert.deepEqual(parseSchedule("on dow=sun,wed,sat").dow.values, [0, 3, 6]);
    });

    test("named months", () => {
        assert.deepEqual(parseSchedule("on month=jan,dec").month.values, [1, 12]);
    });
});

describe("time literals", () => {
    test("HH:MM expands to hour and minute", () => {
        const spec = parseSchedule("on 09:30");
        assert.deepEqual(spec.hour.values, [9]);
        assert.deepEqual(spec.minute.values, [30]);
        assert.deepEqual(spec.second.values, [0]);
    });

    test("HH:MM:SS expands to seconds as well", () => {
        assert.deepEqual(parseSchedule("on 09:30:15").second.values, [15]);
    });

    test("leading zeros are accepted", () => {
        assert.deepEqual(parseSchedule("on 03:00").hour.values, [3]);
    });

    test("a time literal is equivalent to naming the fields", () => {
        const after = at("2026-08-17T00:00:00Z");
        assert.equal(
            nextFire(parseSchedule("on 09:30"), after),
            nextFire(parseSchedule("on hour=9 minute=30"), after));
    });
});

describe("rejected combinations", () => {
    test("day and dow both restricted", () => {
        const thrown = captureError(() => parseSchedule("on day=1 dow=mon"));
        assert.equal(thrown.code, ScheduleErrorCode.DayAmbiguous);
    });

    test("nth without dow", () => {
        const thrown = captureError(() => parseSchedule("on nth=2 hour=10"));
        assert.equal(thrown.code, ScheduleErrorCode.NthWithoutDow);
    });

    test("errors carry the offending character offset", () => {
        const thrown = captureError(() => parseSchedule("on hour=3 minute=99"));
        assert.equal(thrown.code, ScheduleErrorCode.ValueRange);
        assert.equal(thrown.offset, 17);
    });
});

describe("defaults", () => {
    test("timezone defaults to UTC", () => {
        assert.equal(parseSchedule("on hour=3").tz, "UTC");
    });

    test("dst policies default to firing at the gap end and the first fold", () => {
        const spec = parseSchedule("on hour=3");
        assert.equal(spec.dstGap, "fire_at_gap_end");
        assert.equal(spec.dstFold, "fire_first");
    });
});

describe("intervals", () => {
    test("fixed delay measures from the instant passed in", () => {
        const spec = parseSchedule("every 5m");
        assert.equal(spec.mode, "fixed_delay");
        assert.equal(nextFire(spec, at("2026-08-17T00:02:13Z")), at("2026-08-17T00:07:13Z"));
    });

    test("an anchor implies fixed rate and snaps to the grid", () => {
        const spec = parseSchedule("every 15m from 2026-01-01T00:00:00Z");
        assert.equal(spec.mode, "fixed_rate");
        assert.equal(nextFire(spec, at("2026-08-17T04:07:00Z")), at("2026-08-17T04:15:00Z"));
    });

    test("jitter is parsed but not applied by the evaluator", () => {
        const spec = parseSchedule("every 1h jitter 30s");
        assert.equal(spec.jitterMs, 30_000);
        assert.equal(nextFire(spec, at("2026-08-17T00:00:00Z")), at("2026-08-17T01:00:00Z"));
    });
});

describe("never fires again", () => {
    test("a one shot in the past", () => {
        const spec = parseSchedule("at 2026-01-01T00:00:00Z");
        assert.equal(nextFire(spec, at("2026-08-17T00:00:00Z")), null);
    });

    test("a calendar that can never match", () => {
        const spec = parseSchedule("on month=2 day=30");
        assert.equal(nextFire(spec, at("2026-08-17T00:00:00Z")), null);
    });
});

describe("specs are reusable", () => {
    test("one spec drives many evaluations", () => {
        const spec = parseSchedule("on hour=3");
        let cursor = at("2026-08-17T00:00:00Z");
        const fires = [];

        for (let i = 0; i < 3; i++) {
            cursor = nextFire(spec, cursor);
            fires.push(new Date(cursor).toISOString());
        }

        assert.deepEqual(fires, [
            "2026-08-17T03:00:00.000Z",
            "2026-08-18T03:00:00.000Z",
            "2026-08-19T03:00:00.000Z"
        ]);
    });
});
