// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Runs the shared conformance fixtures against the compiled build. The .NET
// runner in Tide.Asgard.Scheduler.Tests reads the same file, so a disagreement
// between the two is a bug in one binding rather than a difference of opinion.
//
// Requires: npm run build:cjs

const { test, describe } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");

const { parseSchedule, nextFire } = require(
    path.join(__dirname, "..", "..", "dist", "cjs", "scheduler", "index.js"));

const fixtures = JSON.parse(fs.readFileSync(
    path.join(__dirname, "..", "fixtures", "schedule-expression.json"), "utf8"));

function iso(ms) {
    return new Date(ms).toISOString().replace(".000Z", "Z");
}

function captureError(fn) {
    try {
        fn();
    } catch (err) {
        return err;
    }
    return null;
}

describe("fixtures: fire sequences", () => {
    for (const fixture of fixtures.sequences) {
        test(fixture.name, () => {
            const spec = parseSchedule(fixture.expr);
            let cursor = Date.parse(fixture.after);
            const actual = [];

            for (let i = 0; i < fixture.expect.length; i++) {
                const fire = nextFire(spec, cursor);
                if (fire === null) { actual.push(null); break; }
                actual.push(iso(fire));
                cursor = fire;
            }

            assert.deepEqual(actual, fixture.expect, fixture.expr);
        });
    }
});

describe("fixtures: parse errors", () => {
    for (const fixture of fixtures.errors) {
        test(fixture.name, () => {
            const thrown = captureError(() => parseSchedule(fixture.expr));
            assert.ok(thrown, `${fixture.expr}: expected ${fixture.code}, parsed successfully`);
            assert.equal(thrown.code, fixture.code, `${fixture.expr}: code`);
            assert.equal(thrown.offset, fixture.offset, `${fixture.expr}: offset`);
        });
    }
});
