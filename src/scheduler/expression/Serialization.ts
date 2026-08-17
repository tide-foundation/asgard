// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { ScheduleErrorCode, ScheduleParseError } from "./Errors";
import { FieldSet } from "./FieldSet";
import {
    CalendarSpec, DstFoldPolicy, DstGapPolicy, FIELD_RANGES, IntervalMode,
    IntervalSpec, OnceSpec, ScheduleSpec
} from "./Spec";

// Schedules are stored as this canonical form rather than as their original
// text, and are never re-parsed at fire time. That way a later change to the
// expression language cannot reinterpret a schedule that is already running.
// Keep the original text alongside it for display only.
export const SPEC_VERSION = 1;

type SerializedField = number[] | "any";

export function specToJson(spec: ScheduleSpec): Record<string, unknown> {
    switch (spec.kind) {
        case "once":
            return { kind: "once", v: SPEC_VERSION, atMs: spec.atMs };

        case "interval":
            return {
                kind: "interval",
                v: SPEC_VERSION,
                periodMs: spec.periodMs,
                anchorMs: spec.anchorMs,
                jitterMs: spec.jitterMs,
                mode: spec.mode
            };

        case "calendar":
            return {
                kind: "calendar",
                v: SPEC_VERSION,
                tz: spec.tz,
                second: fieldToJson(spec.second),
                minute: fieldToJson(spec.minute),
                hour: fieldToJson(spec.hour),
                day: fieldToJson(spec.day),
                dayLast: spec.dayLast,
                dow: fieldToJson(spec.dow),
                nth: spec.nth,
                month: fieldToJson(spec.month),
                dstGap: spec.dstGap,
                dstFold: spec.dstFold
            };
    }
}

export function specFromJson(json: Record<string, unknown>): ScheduleSpec {
    const version = json.v;
    if (version !== SPEC_VERSION) {
        throw badSpec(`unsupported spec version ${String(version)}, expected ${SPEC_VERSION}`);
    }

    switch (json.kind) {
        case "once":
            return { kind: "once", atMs: requireNumber(json.atMs, "atMs") };

        case "interval":
            return {
                kind: "interval",
                periodMs: requireNumber(json.periodMs, "periodMs"),
                anchorMs: json.anchorMs === null || json.anchorMs === undefined
                    ? null
                    : requireNumber(json.anchorMs, "anchorMs"),
                jitterMs: requireNumber(json.jitterMs, "jitterMs"),
                mode: requireEnum(json.mode, "mode", [IntervalMode.FixedDelay, IntervalMode.FixedRate])
            };

        case "calendar":
            return {
                kind: "calendar",
                tz: requireTimeZone(json.tz),
                second: fieldFromJson(json.second, "second", FIELD_RANGES.second),
                minute: fieldFromJson(json.minute, "minute", FIELD_RANGES.minute),
                hour: fieldFromJson(json.hour, "hour", FIELD_RANGES.hour),
                day: fieldFromJson(json.day, "day", FIELD_RANGES.day),
                dayLast: requireBoolean(json.dayLast, "dayLast"),
                dow: fieldFromJson(json.dow, "dow", FIELD_RANGES.dow),
                nth: json.nth === null || json.nth === undefined
                    ? null
                    : requireNumber(json.nth, "nth"),
                month: fieldFromJson(json.month, "month", FIELD_RANGES.month),
                dstGap: requireEnum(json.dstGap, "dstGap", [DstGapPolicy.FireAtGapEnd, DstGapPolicy.Skip]),
                dstFold: requireEnum(json.dstFold, "dstFold", [DstFoldPolicy.FireFirst, DstFoldPolicy.FireLast])
            };

        default:
            throw badSpec(`unknown spec kind '${String(json.kind)}'`);
    }
}

export function specToString(spec: ScheduleSpec): string {
    return JSON.stringify(specToJson(spec));
}

export function specFromString(text: string): ScheduleSpec {
    let parsed: unknown;
    try {
        parsed = JSON.parse(text);
    } catch {
        throw badSpec("spec is not valid JSON");
    }
    if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) {
        throw badSpec("spec must be a JSON object");
    }
    return specFromJson(parsed as Record<string, unknown>);
}

// Unrestricted fields serialize as "any" rather than every value in range, which
// keeps stored specs readable and small.
function fieldToJson(field: FieldSet): SerializedField {
    return field.isAny ? "any" : Array.from(field.values);
}

function fieldFromJson(value: unknown, name: string, range: { min: number; max: number }): FieldSet {
    if (value === "any") return FieldSet.any(range.min, range.max);

    if (!Array.isArray(value) || value.length === 0) {
        throw badSpec(`${name} must be "any" or a non empty array`);
    }
    for (const v of value) {
        if (typeof v !== "number" || !Number.isInteger(v) || v < range.min || v > range.max) {
            throw badSpec(`${name} value ${String(v)} is outside ${range.min}..${range.max}`);
        }
    }
    return FieldSet.of(range.min, range.max, value as number[]);
}

function requireNumber(value: unknown, name: string): number {
    if (typeof value !== "number" || !Number.isFinite(value)) {
        throw badSpec(`${name} must be a number`);
    }
    return value;
}

function requireString(value: unknown, name: string): string {
    if (typeof value !== "string") throw badSpec(`${name} must be a string`);
    return value;
}

// Validated on load rather than at fire time, so a schedule with a zone this
// host does not know fails where someone is watching.
function requireTimeZone(value: unknown): string {
    const id = requireString(value, "tz");
    try {
        new Intl.DateTimeFormat("en-US", { timeZone: id });
    } catch {
        throw badSpec(`unknown timezone '${id}'`);
    }
    return id;
}

function requireBoolean(value: unknown, name: string): boolean {
    if (typeof value !== "boolean") throw badSpec(`${name} must be a boolean`);
    return value;
}

function requireEnum<T extends string>(value: unknown, name: string, allowed: readonly T[]): T {
    if (typeof value !== "string" || allowed.indexOf(value as T) < 0) {
        throw badSpec(`${name} must be one of ${allowed.join(", ")}`);
    }
    return value as T;
}

function badSpec(detail: string): ScheduleParseError {
    return new ScheduleParseError(ScheduleErrorCode.BadSpec, 0, detail);
}
