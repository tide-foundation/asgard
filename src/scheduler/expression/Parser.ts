// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { ScheduleErrorCode, ScheduleParseError } from "./Errors";
import { FieldSet } from "./FieldSet";
import { parseDuration, parseInstant } from "./Duration";
import { Token, tokenize } from "./Tokenizer";
import {
    CalendarSpec, DOW_NAMES, DstFoldPolicy, DstGapPolicy, FIELD_RANGES,
    IntervalMode, IntervalSpec, MONTH_NAMES, OnceSpec, ScheduleSpec
} from "./Spec";

// Granularity ladder, coarsest first. The defaulting rule keys off this: any
// field finer than the finest one the user named collapses to its floor value,
// and any field coarser than it stays unrestricted.
const LEVEL_MONTH = 0;
const LEVEL_DAY = 1;
const LEVEL_HOUR = 2;
const LEVEL_MINUTE = 3;
const LEVEL_SECOND = 4;

interface RawField {
    readonly value: string;
    readonly valueOffset: number;
    readonly token: Token;
}

export function parseSchedule(input: string): ScheduleSpec {
    const tokens = tokenize(input);
    if (tokens.length === 0) {
        throw new ScheduleParseError(ScheduleErrorCode.Empty, 0, "expression is empty");
    }

    const leader = tokens[0];
    switch (leader.text.toLowerCase()) {
        case "on": return parseCalendar(tokens);
        case "every": return parseInterval(tokens);
        case "at": return parseOnce(tokens);
        default:
            throw new ScheduleParseError(
                ScheduleErrorCode.UnknownLeader, leader.offset,
                `expected 'on', 'every' or 'at', got '${leader.text}'`);
    }
}

// at <iso-instant>
function parseOnce(tokens: Token[]): OnceSpec {
    if (tokens.length < 2) {
        throw new ScheduleParseError(
            ScheduleErrorCode.MissingValue, tokens[0].offset + tokens[0].text.length,
            "'at' requires an instant");
    }
    if (tokens.length > 2) {
        throw new ScheduleParseError(
            ScheduleErrorCode.Trailing, tokens[2].offset, `unexpected '${tokens[2].text}'`);
    }
    return { kind: "once", atMs: parseInstant(tokens[1].text, tokens[1].offset) };
}

// every <duration> [from <instant>] [jitter <duration>] [mode=fixed_rate|fixed_delay]
function parseInterval(tokens: Token[]): IntervalSpec {
    if (tokens.length < 2) {
        throw new ScheduleParseError(
            ScheduleErrorCode.MissingValue, tokens[0].offset + tokens[0].text.length,
            "'every' requires a duration");
    }

    const periodMs = parseDuration(tokens[1].text.toLowerCase(), tokens[1].offset);
    let anchorMs: number | null = null;
    let jitterMs = 0;
    let mode = IntervalMode.FixedDelay;

    let i = 2;
    while (i < tokens.length) {
        const t = tokens[i];
        const lower = t.text.toLowerCase();

        if (lower === "from") {
            anchorMs = parseInstant(requireNext(tokens, i, "'from'").text, tokens[i + 1].offset);
            // An explicit grid anchor only makes sense on a fixed rate schedule.
            mode = IntervalMode.FixedRate;
            i += 2;
        } else if (lower === "jitter") {
            jitterMs = parseDuration(
                requireNext(tokens, i, "'jitter'").text.toLowerCase(), tokens[i + 1].offset);
            i += 2;
        } else if (lower.startsWith("mode=")) {
            const raw = lower.slice("mode=".length);
            const valueOffset = t.offset + "mode=".length;
            if (raw === IntervalMode.FixedRate) mode = IntervalMode.FixedRate;
            else if (raw === IntervalMode.FixedDelay) mode = IntervalMode.FixedDelay;
            else throw new ScheduleParseError(
                ScheduleErrorCode.BadValue, valueOffset,
                `mode must be fixed_rate or fixed_delay, got '${raw}'`);
            i += 1;
        } else {
            throw new ScheduleParseError(
                ScheduleErrorCode.Trailing, t.offset, `unexpected '${t.text}'`);
        }
    }

    if (mode === IntervalMode.FixedDelay && anchorMs !== null) {
        throw new ScheduleParseError(
            ScheduleErrorCode.BadValue, tokens[0].offset,
            "'from' anchors a grid and cannot be combined with mode=fixed_delay");
    }

    return { kind: "interval", periodMs, anchorMs, jitterMs, mode };
}

function requireNext(tokens: Token[], i: number, what: string): Token {
    if (i + 1 >= tokens.length) {
        throw new ScheduleParseError(
            ScheduleErrorCode.MissingValue, tokens[i].offset + tokens[i].text.length,
            `${what} requires a value`);
    }
    return tokens[i + 1];
}

// on <field>=<value> ...
function parseCalendar(tokens: Token[]): CalendarSpec {
    const fields = new Map<string, RawField>();

    for (let i = 1; i < tokens.length; i++) {
        const t = tokens[i];
        const eq = t.text.indexOf("=");
        if (eq < 0) {
            // A bare HH:MM is shorthand for the hour and minute fields, since a
            // daily time is by far the most common schedule.
            if (t.text.indexOf(":") >= 0) {
                expandTimeLiteral(t, fields);
                continue;
            }
            throw new ScheduleParseError(
                ScheduleErrorCode.UnknownField, t.offset,
                `expected name=value, got '${t.text}'`);
        }
        const name = t.text.slice(0, eq).toLowerCase();
        if (eq === t.text.length - 1) {
            throw new ScheduleParseError(
                ScheduleErrorCode.MissingValue, t.offset + eq + 1, `'${name}' has no value`);
        }
        if (!KNOWN_FIELDS.has(name)) {
            throw new ScheduleParseError(
                ScheduleErrorCode.UnknownField, t.offset, `unknown field '${name}'`);
        }
        if (fields.has(name)) {
            throw new ScheduleParseError(
                ScheduleErrorCode.DuplicateField, t.offset, `field '${name}' set twice`);
        }
        fields.set(name, { value: t.text.slice(eq + 1), valueOffset: t.offset + eq + 1, token: t });
    }

    const tz = parseTimeZone(fields.get("tz"));
    const dstGap = parseEnumField(
        fields.get("dstgap"), DstGapPolicy.FireAtGapEnd,
        [DstGapPolicy.FireAtGapEnd, DstGapPolicy.Skip], "dstgap");
    const dstFold = parseEnumField(
        fields.get("dstfold"), DstFoldPolicy.FireFirst,
        [DstFoldPolicy.FireFirst, DstFoldPolicy.FireLast], "dstfold");

    const monthRaw = fields.get("month");
    const dayRaw = fields.get("day");
    const dowRaw = fields.get("dow");
    const nthRaw = fields.get("nth");
    const hourRaw = fields.get("hour");
    const minuteRaw = fields.get("minute");
    const secondRaw = fields.get("second");

    // Which rungs of the granularity ladder the user actually touched.
    const touched = [
        monthRaw !== undefined,
        dayRaw !== undefined || dowRaw !== undefined || nthRaw !== undefined,
        hourRaw !== undefined,
        minuteRaw !== undefined,
        secondRaw !== undefined
    ];

    let finest = -1;
    for (let level = 0; level < touched.length; level++) {
        if (touched[level]) finest = level;
    }
    if (finest < 0) {
        throw new ScheduleParseError(
            ScheduleErrorCode.Empty, tokens[0].offset, "'on' requires at least one field");
    }

    let dayLast = false;
    let day: FieldSet;
    if (dayRaw !== undefined) {
        if (dayRaw.value.toLowerCase() === "last") {
            dayLast = true;
            day = FieldSet.any(FIELD_RANGES.day.min, FIELD_RANGES.day.max);
        } else {
            day = parseValueList(dayRaw, FIELD_RANGES.day.min, FIELD_RANGES.day.max, null);
        }
    } else {
        day = defaultFor(LEVEL_DAY, finest, FIELD_RANGES.day.min, FIELD_RANGES.day.max);
    }

    const dow = dowRaw !== undefined
        ? parseValueList(dowRaw, FIELD_RANGES.dow.min, FIELD_RANGES.dow.max, DOW_NAMES)
        : FieldSet.any(FIELD_RANGES.dow.min, FIELD_RANGES.dow.max);

    const month = monthRaw !== undefined
        ? parseValueList(monthRaw, FIELD_RANGES.month.min, FIELD_RANGES.month.max, MONTH_NAMES)
        : FieldSet.any(FIELD_RANGES.month.min, FIELD_RANGES.month.max);

    const hour = hourRaw !== undefined
        ? parseValueList(hourRaw, FIELD_RANGES.hour.min, FIELD_RANGES.hour.max, null)
        : defaultFor(LEVEL_HOUR, finest, FIELD_RANGES.hour.min, FIELD_RANGES.hour.max);

    const minute = minuteRaw !== undefined
        ? parseValueList(minuteRaw, FIELD_RANGES.minute.min, FIELD_RANGES.minute.max, null)
        : defaultFor(LEVEL_MINUTE, finest, FIELD_RANGES.minute.min, FIELD_RANGES.minute.max);

    const second = secondRaw !== undefined
        ? parseValueList(secondRaw, FIELD_RANGES.second.min, FIELD_RANGES.second.max, null)
        : defaultFor(LEVEL_SECOND, finest, FIELD_RANGES.second.min, FIELD_RANGES.second.max);

    const nth = nthRaw !== undefined ? parseNth(nthRaw) : null;

    if (nth !== null && dowRaw === undefined) {
        throw new ScheduleParseError(
            ScheduleErrorCode.NthWithoutDow, nthRaw!.token.offset,
            "nth requires dow to select which weekday to count");
    }

    // Standard cron ORs day and dow when both are restricted, which surprises
    // people. Reject the combination instead of inheriting the ambiguity.
    const dayRestricted = dayLast || !day.isAny;
    if (dayRestricted && !dow.isAny) {
        throw new ScheduleParseError(
            ScheduleErrorCode.DayAmbiguous, (dayRaw ?? dowRaw)!.token.offset,
            "day and dow cannot both be restricted");
    }

    return {
        kind: "calendar",
        tz, second, minute, hour, day, dayLast, dow, nth, month, dstGap, dstFold
    };
}

// Rewrites HH:MM or HH:MM:SS into the fields it stands for, so the rest of the
// parser and the defaulting rule see no difference between "on 09:30" and
// "on hour=9 minute=30".
function expandTimeLiteral(t: Token, fields: Map<string, RawField>): void {
    const parts = t.text.split(":");
    if (parts.length < 2 || parts.length > 3) {
        throw new ScheduleParseError(
            ScheduleErrorCode.BadValue, t.offset,
            `expected HH:MM or HH:MM:SS, got '${t.text}'`);
    }

    const names = ["hour", "minute", "second"];
    let cursor = 0;

    for (let i = 0; i < parts.length; i++) {
        const offset = t.offset + cursor;
        cursor += parts[i].length + 1;

        if (!/^\d{1,2}$/.test(parts[i])) {
            throw new ScheduleParseError(
                ScheduleErrorCode.BadValue, offset, `'${parts[i]}' is not a valid ${names[i]}`);
        }
        if (fields.has(names[i])) {
            throw new ScheduleParseError(
                ScheduleErrorCode.DuplicateField, t.offset, `field '${names[i]}' set twice`);
        }
        fields.set(names[i], { value: parts[i], valueOffset: offset, token: t });
    }
}

const KNOWN_FIELDS = new Set([
    "second", "minute", "hour", "day", "dow", "month", "nth", "tz", "dstgap", "dstfold"
]);

// Fields finer than the finest named one collapse to their floor. Coarser
// fields stay unrestricted.
function defaultFor(level: number, finest: number, min: number, max: number): FieldSet {
    return level > finest ? FieldSet.single(min, max, min) : FieldSet.any(min, max);
}

function parseNth(raw: RawField): number {
    const lower = raw.value.toLowerCase();
    if (lower === "last") return -1;
    if (!/^[1-5]$/.test(lower)) {
        throw new ScheduleParseError(
            ScheduleErrorCode.ValueRange, raw.valueOffset,
            `nth must be 1 to 5 or 'last', got '${raw.value}'`);
    }
    return Number(lower);
}

function parseTimeZone(raw: RawField | undefined): string {
    if (raw === undefined) return "UTC";
    try {
        // Constructing a formatter is the stdlib way to validate a zone id.
        new Intl.DateTimeFormat("en-US", { timeZone: raw.value });
    } catch {
        throw new ScheduleParseError(
            ScheduleErrorCode.UnknownTimeZone, raw.valueOffset, `unknown timezone '${raw.value}'`);
    }
    return raw.value;
}

function parseEnumField<T extends string>(
    raw: RawField | undefined, fallback: T, allowed: readonly T[], name: string): T {
    if (raw === undefined) return fallback;
    const lower = raw.value.toLowerCase() as T;
    if (allowed.indexOf(lower) < 0) {
        throw new ScheduleParseError(
            ScheduleErrorCode.BadValue, raw.valueOffset,
            `${name} must be one of ${allowed.join(", ")}, got '${raw.value}'`);
    }
    return lower;
}

// Accepts *, */n, a, a-b, a-b/n, a/n and comma separated combinations.
function parseValueList(
    raw: RawField, min: number, max: number, names: Readonly<Record<string, number>> | null): FieldSet {
    const values: number[] = [];
    let cursor = 0;

    for (const part of raw.value.split(",")) {
        const partOffset = raw.valueOffset + cursor;
        cursor += part.length + 1;
        if (part.length === 0) {
            throw new ScheduleParseError(
                ScheduleErrorCode.BadValue, partOffset, "empty list entry");
        }
        parseRangePart(part, partOffset, min, max, names, values);
    }

    return FieldSet.of(min, max, values);
}

function parseRangePart(
    part: string, offset: number, min: number, max: number,
    names: Readonly<Record<string, number>> | null, out: number[]): void {

    let step = 1;
    let body = part;
    const slash = part.indexOf("/");

    if (slash >= 0) {
        body = part.slice(0, slash);
        const stepText = part.slice(slash + 1);
        if (!/^\d+$/.test(stepText)) {
            throw new ScheduleParseError(
                ScheduleErrorCode.BadStep, offset + slash + 1, `step must be a number, got '${stepText}'`);
        }
        step = Number(stepText);
        if (step < 1) {
            throw new ScheduleParseError(
                ScheduleErrorCode.BadStep, offset + slash + 1, "step must be at least 1");
        }
    }

    let lo: number;
    let hi: number;

    if (body === "*") {
        lo = min;
        hi = max;
    } else {
        const dash = findRangeSeparator(body);
        if (dash >= 0) {
            lo = parseScalar(body.slice(0, dash), offset, min, max, names);
            hi = parseScalar(body.slice(dash + 1), offset + dash + 1, min, max, names);
            if (hi < lo) {
                throw new ScheduleParseError(
                    ScheduleErrorCode.BadRange, offset, `range '${body}' ends before it starts`);
            }
        } else {
            lo = parseScalar(body, offset, min, max, names);
            // A bare value with a step runs from that value to the field max,
            // matching how cron reads 10/15.
            hi = slash >= 0 ? max : lo;
        }
    }

    for (let v = lo; v <= hi; v += step) out.push(v);
}

// Only treat a dash as a range separator when it is not the leading character,
// so a future signed value cannot be misread.
function findRangeSeparator(body: string): number {
    return body.indexOf("-", 1);
}

function parseScalar(
    text: string, offset: number, min: number, max: number,
    names: Readonly<Record<string, number>> | null): number {

    if (text.length === 0) {
        throw new ScheduleParseError(ScheduleErrorCode.BadValue, offset, "empty value");
    }

    let value: number;
    if (/^\d+$/.test(text)) {
        value = Number(text);
    } else if (names !== null && names[text.toLowerCase()] !== undefined) {
        value = names[text.toLowerCase()];
    } else {
        throw new ScheduleParseError(
            ScheduleErrorCode.BadValue, offset, `'${text}' is not a valid value`);
    }

    if (value < min || value > max) {
        throw new ScheduleParseError(
            ScheduleErrorCode.ValueRange, offset, `${value} is outside ${min}..${max}`);
    }
    return value;
}
