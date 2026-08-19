// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { FieldSet } from "./FieldSet";

export enum IntervalMode {
    // Next fire is measured from the completion of the previous run, so runs
    // can never overlap. This is the default for "rerun this function".
    FixedDelay = "fixed_delay",
    // Next fire sits on a fixed grid anchored to anchorMs, regardless of how
    // long a run took.
    FixedRate = "fixed_rate"
}

export enum DstGapPolicy {
    // Wall clock time does not exist on this day. Fire when the gap ends.
    FireAtGapEnd = "fire_at_gap_end",
    // Skip the occurrence entirely and look for the next one.
    Skip = "skip"
}

export enum DstFoldPolicy {
    // Wall clock time happens twice. Use the earlier instant.
    FireFirst = "fire_first",
    // Use the later instant.
    FireLast = "fire_last"
}

export interface OnceSpec {
    readonly kind: "once";
    readonly atMs: number;
}

export interface IntervalSpec {
    readonly kind: "interval";
    readonly periodMs: number;
    // Grid anchor for FixedRate. Null means the unix epoch.
    readonly anchorMs: number | null;
    // Applied by the scheduler at enqueue time, not by the evaluator. The
    // evaluator stays deterministic so fixtures can pin its output.
    readonly jitterMs: number;
    readonly mode: IntervalMode;
}

export interface CalendarSpec {
    readonly kind: "calendar";
    readonly tz: string;
    readonly second: FieldSet;
    readonly minute: FieldSet;
    readonly hour: FieldSet;
    readonly day: FieldSet;
    // day=last matches the final day of each month, whatever its length.
    readonly dayLast: boolean;
    readonly dow: FieldSet;
    // Restricts dow to the nth occurrence in the month. 1 to 5, or -1 for last.
    readonly nth: number | null;
    readonly month: FieldSet;
    readonly dstGap: DstGapPolicy;
    readonly dstFold: DstFoldPolicy;
}

export type ScheduleSpec = OnceSpec | IntervalSpec | CalendarSpec;

export const FIELD_RANGES = {
    second: { min: 0, max: 59 },
    minute: { min: 0, max: 59 },
    hour: { min: 0, max: 23 },
    day: { min: 1, max: 31 },
    dow: { min: 0, max: 6 },
    month: { min: 1, max: 12 }
} as const;

// Sunday is 0 to match Date.getUTCDay and DayOfWeek.Sunday.
export const DOW_NAMES: Readonly<Record<string, number>> = {
    sun: 0, mon: 1, tue: 2, wed: 3, thu: 4, fri: 5, sat: 6
};

export const MONTH_NAMES: Readonly<Record<string, number>> = {
    jan: 1, feb: 2, mar: 3, apr: 4, may: 5, jun: 6,
    jul: 7, aug: 8, sep: 9, oct: 10, nov: 11, dec: 12
};
