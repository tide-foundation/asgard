// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Error codes are part of the cross-runtime contract. The .NET parser must emit
// the same code for the same input so shared fixtures can assert on bad input.
export enum ScheduleErrorCode {
    Empty = "E_EMPTY",
    UnknownLeader = "E_UNKNOWN_LEADER",
    UnknownField = "E_UNKNOWN_FIELD",
    DuplicateField = "E_DUPLICATE_FIELD",
    MissingValue = "E_MISSING_VALUE",
    BadValue = "E_BAD_VALUE",
    ValueRange = "E_VALUE_RANGE",
    BadStep = "E_BAD_STEP",
    BadRange = "E_BAD_RANGE",
    DayAmbiguous = "E_DAY_AMBIGUOUS",
    NthWithoutDow = "E_NTH_WITHOUT_DOW",
    UnknownTimeZone = "E_UNKNOWN_TIMEZONE",
    BadDuration = "E_BAD_DURATION",
    BadInstant = "E_BAD_INSTANT",
    Trailing = "E_TRAILING"
}

export class ScheduleParseError extends Error {
    readonly code: ScheduleErrorCode;
    readonly offset: number;

    constructor(code: ScheduleErrorCode, offset: number, detail: string) {
        super(`${code} at ${offset}: ${detail}`);
        this.name = "ScheduleParseError";
        this.code = code;
        this.offset = offset;
    }
}
