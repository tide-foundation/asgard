// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { resolveCivil, toCivil } from "./TimeZone";
import {
    CalendarSpec, DstFoldPolicy, DstGapPolicy, IntervalMode, IntervalSpec,
    OnceSpec, ScheduleSpec
} from "./Spec";

const SECOND_MS = 1000;
const MINUTE_MS = 60_000;
const HOUR_MS = 3_600_000;
const DAY_MS = 86_400_000;

// A calendar spec can legitimately never fire again, for example day=30 with
// month=2, so the search needs a bound. The Gregorian calendar repeats exactly
// every 400 years, weekday alignment included, so anything that has not fired
// within one cycle never will. A shorter horizon would be a guess: day=29
// month=2 already has an 8 year gap across a century that skips its leap day,
// and nth=5 in February is rarer still.
const HORIZON_YEARS = 400;
const MAX_ITERATIONS = 200_000;

// How far past a spring forward gap to look for the moment the clock resumes.
const GAP_PROBE_MINUTES = 240;

// First instant strictly after afterMs at which this schedule fires, or null
// when it never fires again.
//
// For IntervalMode.FixedDelay the caller passes the completion instant of the
// previous run, since that mode measures from completion rather than from a grid.
// Jitter is deliberately not applied here so this function stays deterministic
// and testable against shared fixtures.
export function nextFire(spec: ScheduleSpec, afterMs: number): number | null {
    switch (spec.kind) {
        case "once": return nextOnce(spec, afterMs);
        case "interval": return nextInterval(spec, afterMs);
        case "calendar": return nextCalendar(spec, afterMs);
    }
}

function nextOnce(spec: OnceSpec, afterMs: number): number | null {
    return spec.atMs > afterMs ? spec.atMs : null;
}

function nextInterval(spec: IntervalSpec, afterMs: number): number {
    if (spec.mode === IntervalMode.FixedDelay) {
        return afterMs + spec.periodMs;
    }
    const anchor = spec.anchorMs ?? 0;
    const elapsed = afterMs - anchor;
    const ticks = Math.floor(elapsed / spec.periodMs) + 1;
    return anchor + ticks * spec.periodMs;
}

function nextCalendar(spec: CalendarSpec, afterMs: number): number | null {
    let p = toCivil(afterMs, spec.tz);
    const startYear = utcYear(p);

    for (let i = 0; i < MAX_ITERATIONS; i++) {
        const d = new Date(p);
        const year = d.getUTCFullYear();
        if (year > startYear + HORIZON_YEARS) return null;

        const month = d.getUTCMonth() + 1;
        const nextMonth = spec.month.next(month);
        if (nextMonth < 0) { p = startOfMonth(year + 1, 1); continue; }
        if (nextMonth !== month) { p = startOfMonth(year, nextMonth); continue; }

        if (!dayMatches(spec, d)) { p = startOfDay(p) + DAY_MS; continue; }

        const hour = d.getUTCHours();
        const nextHour = spec.hour.next(hour);
        if (nextHour < 0) { p = startOfDay(p) + DAY_MS; continue; }
        if (nextHour !== hour) { p = setTime(p, nextHour, 0, 0); continue; }

        const minute = d.getUTCMinutes();
        const nextMinute = spec.minute.next(minute);
        if (nextMinute < 0) { p = setTime(p, hour, 0, 0) + HOUR_MS; continue; }
        if (nextMinute !== minute) { p = setTime(p, hour, nextMinute, 0); continue; }

        const second = d.getUTCSeconds();
        const nextSecond = spec.second.next(second);
        if (nextSecond < 0) { p = setTime(p, hour, minute, 0) + MINUTE_MS; continue; }
        if (nextSecond !== second) { p = setTime(p, hour, minute, nextSecond); continue; }

        // Every calendar field matches. Map the wall clock onto a real instant.
        const instant = resolveInstant(spec, p);
        if (instant !== null && instant > afterMs) return instant;

        // Either the clock does not exist here, or the instant we found is not
        // yet past afterMs. Move on by one second and keep searching.
        p += SECOND_MS;
    }

    return null;
}

function resolveInstant(spec: CalendarSpec, pseudoMs: number): number | null {
    const candidates = resolveCivil(pseudoMs, spec.tz);

    if (candidates.length === 1) return candidates[0];
    if (candidates.length > 1) {
        return spec.dstFold === DstFoldPolicy.FireLast
            ? candidates[candidates.length - 1]
            : candidates[0];
    }
    if (spec.dstGap === DstGapPolicy.Skip) return null;
    return findGapEnd(pseudoMs, spec.tz);
}

// The requested wall clock does not exist. Find the instant the clock resumes,
// which is the first existing wall clock at or after the requested one.
// Walk forward in minutes to bracket the gap, then refine to the second.
function findGapEnd(pseudoMs: number, tz: string): number | null {
    for (let m = 1; m <= GAP_PROBE_MINUTES; m++) {
        if (resolveCivil(pseudoMs + m * MINUTE_MS, tz).length === 0) continue;

        const base = pseudoMs + (m - 1) * MINUTE_MS;
        for (let s = 1; s <= 60; s++) {
            const refined = resolveCivil(base + s * SECOND_MS, tz);
            if (refined.length > 0) return refined[0];
        }
        return resolveCivil(pseudoMs + m * MINUTE_MS, tz)[0];
    }
    return null;
}

function dayMatches(spec: CalendarSpec, d: Date): boolean {
    if (!spec.dow.has(d.getUTCDay())) return false;
    if (spec.nth !== null && !nthMatches(d, spec.nth)) return false;

    const day = d.getUTCDate();
    if (spec.dayLast) {
        return day === daysInMonth(d.getUTCFullYear(), d.getUTCMonth() + 1);
    }
    return spec.day.has(day);
}

function nthMatches(d: Date, nth: number): boolean {
    const day = d.getUTCDate();
    if (nth === -1) {
        return day + 7 > daysInMonth(d.getUTCFullYear(), d.getUTCMonth() + 1);
    }
    return Math.floor((day - 1) / 7) + 1 === nth;
}

function daysInMonth(year: number, month: number): number {
    return new Date(Date.UTC(year, month, 0)).getUTCDate();
}

function utcYear(pseudoMs: number): number {
    return new Date(pseudoMs).getUTCFullYear();
}

function startOfMonth(year: number, month: number): number {
    return Date.UTC(year, month - 1, 1, 0, 0, 0);
}

// UTC days align to exact multiples of DAY_MS, so no calendar call is needed.
function startOfDay(pseudoMs: number): number {
    return Math.floor(pseudoMs / DAY_MS) * DAY_MS;
}

function setTime(pseudoMs: number, hour: number, minute: number, second: number): number {
    return startOfDay(pseudoMs) + hour * HOUR_MS + minute * MINUTE_MS + second * SECOND_MS;
}
