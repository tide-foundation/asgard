// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// A resolved set of allowed values for one calendar field.
// Values are sorted ascending and never empty. Sets are at most 60 entries so
// linear scans are cheap and easier to keep identical across runtimes.
export class FieldSet {
    readonly min: number;
    readonly max: number;
    readonly values: readonly number[];

    private constructor(min: number, max: number, values: number[]) {
        this.min = min;
        this.max = max;
        this.values = values;
    }

    static of(min: number, max: number, values: Iterable<number>): FieldSet {
        const sorted = Array.from(new Set(values)).sort((a, b) => a - b);
        return new FieldSet(min, max, sorted);
    }

    static any(min: number, max: number): FieldSet {
        const values: number[] = [];
        for (let v = min; v <= max; v++) values.push(v);
        return new FieldSet(min, max, values);
    }

    static single(min: number, max: number, value: number): FieldSet {
        return new FieldSet(min, max, [value]);
    }

    // True when every value in range is allowed. Used to detect whether a field
    // actually constrains the search.
    get isAny(): boolean {
        return this.values.length === this.max - this.min + 1;
    }

    has(value: number): boolean {
        return this.values.indexOf(value) >= 0;
    }

    // First allowed value greater than or equal to from, or -1 when the search
    // must roll over into the next larger unit.
    next(from: number): number {
        for (const v of this.values) {
            if (v >= from) return v;
        }
        return -1;
    }
}
