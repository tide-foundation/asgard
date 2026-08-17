// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Runs the shared schedule expression fixtures against the compiled TypeScript
// build. Exits non zero on the first mismatch count so it can gate CI.
// Requires: npm run build:cjs

const fs = require("fs");
const path = require("path");

const { parseSchedule, nextFire } = require(path.join(__dirname, "..", "..", "dist", "cjs", "scheduler", "index.js"));

const fixturePath = path.join(__dirname, "..", "fixtures", "schedule-expression.json");
const fixtures = JSON.parse(fs.readFileSync(fixturePath, "utf8"));

let passed = 0;
const failures = [];

function iso(ms) {
    return new Date(ms).toISOString().replace(".000Z", "Z");
}

for (const test of fixtures.sequences) {
    try {
        const spec = parseSchedule(test.expr);
        let cursor = Date.parse(test.after);
        const actual = [];

        for (let i = 0; i < test.expect.length; i++) {
            const fire = nextFire(spec, cursor);
            if (fire === null) { actual.push(null); break; }
            actual.push(iso(fire));
            cursor = fire;
        }

        const want = JSON.stringify(test.expect);
        const got = JSON.stringify(actual);
        if (want === got) passed++;
        else failures.push(`sequence "${test.name}" [${test.expr}]\n    expected ${want}\n    actual   ${got}`);
    } catch (err) {
        failures.push(`sequence "${test.name}" [${test.expr}]\n    threw ${err.message}`);
    }
}

for (const test of fixtures.errors) {
    let thrown = null;
    try {
        parseSchedule(test.expr);
    } catch (err) {
        thrown = err;
    }

    if (thrown === null) {
        failures.push(`error "${test.name}" [${test.expr}]\n    expected ${test.code}, parsed successfully`);
    } else if (thrown.code !== test.code) {
        failures.push(`error "${test.name}" [${test.expr}]\n    expected ${test.code}, got ${thrown.code} (${thrown.message})`);
    } else if (thrown.offset !== test.offset) {
        failures.push(`error "${test.name}" [${test.expr}]\n    ${test.code} expected at offset ${test.offset}, got ${thrown.offset}`);
    } else {
        passed++;
    }
}

const total = fixtures.sequences.length + fixtures.errors.length;
if (failures.length > 0) {
    console.error(`FAIL ${failures.length}/${total}\n`);
    for (const f of failures) console.error("  " + f + "\n");
    process.exit(1);
}

console.log(`PASS ${passed}/${total} schedule expression fixtures`);
