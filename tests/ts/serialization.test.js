// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Requires: npm run build:cjs

const { test, describe } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const {
    parseSchedule, nextFire, specToJson, specFromJson, specToString, specFromString,
    ScheduleErrorCode, SPEC_VERSION
} = require(path.join(__dirname, "..", "..", "dist", "cjs", "scheduler", "index.js"));

const fixtures = JSON.parse(fs.readFileSync(
    path.join(__dirname, "..", "fixtures", "schedule-expression.json"), "utf8"));

function captureError(fn) {
    try {
        fn();
    } catch (err) {
        return err;
    }
    return null;
}

// The property that matters: a spec that has been through storage behaves
// exactly like one straight from the parser. Reusing the conformance fixtures
// exercises it across every expression shape rather than a hand picked few.
describe("round trip preserves behaviour", () => {
    for (const fixture of fixtures.sequences) {
        test(fixture.name, () => {
            const direct = parseSchedule(fixture.expr);
            const restored = specFromString(specToString(direct));

            let a = Date.parse(fixture.after);
            let b = a;

            for (let i = 0; i < fixture.expect.length; i++) {
                a = nextFire(direct, a);
                b = nextFire(restored, b);
                assert.equal(b, a, `${fixture.expr} at step ${i}`);
                if (a === null) break;
            }
        });
    }
});

describe("shape", () => {
    test("unrestricted fields collapse to any", () => {
        const json = specToJson(parseSchedule("on hour=3"));
        assert.equal(json.day, "any");
        assert.equal(json.dow, "any");
        assert.equal(json.month, "any");
        assert.deepEqual(json.hour, [3]);
        assert.deepEqual(json.minute, [0]);
    });

    test("carries a version", () => {
        assert.equal(specToJson(parseSchedule("on hour=3")).v, SPEC_VERSION);
    });

    test("interval keeps mode, anchor and jitter", () => {
        const json = specToJson(parseSchedule("every 15m from 2026-01-01T00:00:00Z jitter 30s"));
        assert.equal(json.kind, "interval");
        assert.equal(json.periodMs, 900_000);
        assert.equal(json.jitterMs, 30_000);
        assert.equal(json.mode, "fixed_rate");
        assert.equal(json.anchorMs, Date.parse("2026-01-01T00:00:00Z"));
    });

    test("one shot keeps its instant", () => {
        const json = specToJson(parseSchedule("at 2026-09-01T03:00:00Z"));
        assert.equal(json.kind, "once");
        assert.equal(json.atMs, Date.parse("2026-09-01T03:00:00Z"));
    });

    test("calendar keeps timezone and dst policies", () => {
        const json = specToJson(parseSchedule("on 02:30 tz=Australia/Sydney dstgap=skip dstfold=fire_last"));
        assert.equal(json.tz, "Australia/Sydney");
        assert.equal(json.dstGap, "skip");
        assert.equal(json.dstFold, "fire_last");
    });
});

describe("rejects bad stored specs", () => {
    test("wrong version", () => {
        const json = specToJson(parseSchedule("on hour=3"));
        json.v = 999;
        assert.equal(captureError(() => specFromJson(json)).code, ScheduleErrorCode.BadSpec);
    });

    test("unknown kind", () => {
        assert.equal(
            captureError(() => specFromJson({ kind: "weekly", v: SPEC_VERSION })).code,
            ScheduleErrorCode.BadSpec);
    });

    test("field value out of range", () => {
        const json = specToJson(parseSchedule("on hour=3"));
        json.hour = [99];
        assert.equal(captureError(() => specFromJson(json)).code, ScheduleErrorCode.BadSpec);
    });

    test("empty field array", () => {
        const json = specToJson(parseSchedule("on hour=3"));
        json.hour = [];
        assert.equal(captureError(() => specFromJson(json)).code, ScheduleErrorCode.BadSpec);
    });

    test("unknown timezone", () => {
        const json = specToJson(parseSchedule("on hour=3"));
        json.tz = "Mars/Olympus";
        assert.equal(captureError(() => specFromJson(json)).code, ScheduleErrorCode.BadSpec);
    });

    test("not json", () => {
        assert.equal(captureError(() => specFromString("{nope")).code, ScheduleErrorCode.BadSpec);
    });
});
