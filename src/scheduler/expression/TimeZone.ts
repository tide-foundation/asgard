// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// The only platform specific code in the expression subsystem. Everything else
// is integer arithmetic shared with the .NET implementation.
//
// Civil times are carried as a "pseudo epoch": the wall clock fields packed
// through Date.UTC. That gives calendar arithmetic with no timezone attached,
// which is what the evaluator searches over.

const SECOND_MS = 1000;
const DAY_MS = 86_400_000;

const formatters = new Map<string, Intl.DateTimeFormat>();

function formatter(tz: string): Intl.DateTimeFormat {
    let f = formatters.get(tz);
    if (f === undefined) {
        // Caching matters. formatToParts is the hot cost in the evaluator.
        f = new Intl.DateTimeFormat("en-US", {
            timeZone: tz,
            hourCycle: "h23",
            year: "numeric",
            month: "2-digit",
            day: "2-digit",
            hour: "2-digit",
            minute: "2-digit",
            second: "2-digit"
        });
        formatters.set(tz, f);
    }
    return f;
}

// Wall clock in tz at the given instant, as a pseudo epoch.
export function toCivil(instantMs: number, tz: string): number {
    const whole = Math.floor(instantMs / SECOND_MS) * SECOND_MS;
    const parts: Record<string, string> = {};
    for (const p of formatter(tz).formatToParts(whole)) {
        if (p.type !== "literal") parts[p.type] = p.value;
    }
    // Some ICU builds report midnight as hour 24 despite hourCycle h23.
    const hour = Number(parts.hour) % 24;
    return Date.UTC(
        Number(parts.year), Number(parts.month) - 1, Number(parts.day),
        hour, Number(parts.minute), Number(parts.second));
}

export function utcOffsetAt(instantMs: number, tz: string): number {
    const whole = Math.floor(instantMs / SECOND_MS) * SECOND_MS;
    return toCivil(whole, tz) - whole;
}

// Maps a wall clock back to real instants. Returns:
//   []        the clock never reads this, it falls in a spring forward gap
//   [t]       the normal case
//   [t1, t2]  the clock reads this twice, it falls in a fall back overlap
//
// Bracketing plus or minus a day captures the offset on both sides of any
// transition. Filtering by round trip is what separates a gap from an overlap.
export function resolveCivil(pseudoMs: number, tz: string): number[] {
    const offBefore = utcOffsetAt(pseudoMs - DAY_MS, tz);
    const offAfter = utcOffsetAt(pseudoMs + DAY_MS, tz);

    const candidates = offBefore === offAfter
        ? [pseudoMs - offBefore]
        : [pseudoMs - offBefore, pseudoMs - offAfter];

    const valid: number[] = [];
    for (const c of candidates) {
        if (toCivil(c, tz) === pseudoMs && valid.indexOf(c) < 0) valid.push(c);
    }
    valid.sort((a, b) => a - b);
    return valid;
}
