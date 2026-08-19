// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { ScheduleErrorCode, ScheduleParseError } from "./Errors";

const UNIT_MS: Readonly<Record<string, number>> = {
    ms: 1,
    s: 1000,
    m: 60_000,
    h: 3_600_000,
    d: 86_400_000
};

// Parses compound duration literals such as 30s, 1h30m or 500ms.
// Units must appear at most once and in descending order of size so that
// "1h30m" is valid and "30m1h" is not.
export function parseDuration(text: string, offset = 0): number {
    if (text.length === 0) {
        throw new ScheduleParseError(ScheduleErrorCode.BadDuration, offset, "empty duration");
    }

    let total = 0;
    let i = 0;
    let lastUnitMs = Number.MAX_SAFE_INTEGER;
    let matchedAny = false;

    while (i < text.length) {
        const numStart = i;
        while (i < text.length && text[i] >= "0" && text[i] <= "9") i++;
        if (i === numStart) {
            throw new ScheduleParseError(
                ScheduleErrorCode.BadDuration, offset + i, `expected digits, got '${text[i]}'`);
        }
        const value = Number(text.slice(numStart, i));

        const unitStart = i;
        while (i < text.length && text[i] >= "a" && text[i] <= "z") i++;
        const unit = text.slice(unitStart, i);
        if (unit.length === 0) {
            throw new ScheduleParseError(
                ScheduleErrorCode.BadDuration, offset + unitStart, "missing unit");
        }

        const unitMs = UNIT_MS[unit];
        if (unitMs === undefined) {
            throw new ScheduleParseError(
                ScheduleErrorCode.BadDuration, offset + unitStart, `unknown unit '${unit}'`);
        }
        if (unitMs >= lastUnitMs) {
            throw new ScheduleParseError(
                ScheduleErrorCode.BadDuration, offset + unitStart,
                `unit '${unit}' out of order or repeated`);
        }

        lastUnitMs = unitMs;
        total += value * unitMs;
        matchedAny = true;
    }

    if (!matchedAny) {
        throw new ScheduleParseError(ScheduleErrorCode.BadDuration, offset, "empty duration");
    }
    if (total <= 0) {
        throw new ScheduleParseError(ScheduleErrorCode.BadDuration, offset, "duration must be positive");
    }
    return total;
}

// ISO 8601 instant. Date.parse handles the format natively, but it also accepts
// a lot of non-ISO input, so require an explicit zone designator first.
export function parseInstant(text: string, offset = 0): number {
    if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(\.\d+)?(Z|[+-]\d{2}:\d{2})$/.test(text)) {
        throw new ScheduleParseError(
            ScheduleErrorCode.BadInstant, offset, `expected ISO 8601 instant, got '${text}'`);
    }
    const ms = Date.parse(text);
    if (Number.isNaN(ms)) {
        throw new ScheduleParseError(ScheduleErrorCode.BadInstant, offset, `unparseable instant '${text}'`);
    }
    return ms;
}
