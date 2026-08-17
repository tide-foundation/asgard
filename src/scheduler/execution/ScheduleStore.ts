// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { ScheduleSpec } from "../expression/Spec";
import { MisfirePolicy } from "./MisfirePolicy";

export interface ScheduleRecord {
    readonly name: string;
    readonly handler: string;
    readonly payload: unknown;
    // Kept for display and for admin listings. The spec is what actually runs.
    readonly expr: string;
    readonly spec: ScheduleSpec;
    readonly enabled: boolean;
    readonly misfire: MisfirePolicy;
    readonly maxAttempts: number | null;
    // Null when the next occurrence is chained on settle rather than
    // materialized, or when the schedule can never fire again.
    readonly nextFireAtMs: number | null;
    readonly lastFireAtMs: number | null;
    readonly updatedAtMs: number;
}

export interface ScheduleUpsert {
    readonly name: string;
    readonly handler: string;
    readonly payload?: unknown;
    readonly expr: string;
    readonly spec: ScheduleSpec;
    readonly misfire: MisfirePolicy;
    readonly maxAttempts: number | null;
    // Used only when the schedule is new, or when its spec has changed.
    readonly nextFireAtMs: number | null;
}

// Where recurring schedules live. Separate from JobStore because a schedule is a
// definition while a run is an occurrence, and because a host may reasonably
// want durable runs with schedules still declared in code.
export interface ScheduleStore {
    // Registering an existing schedule updates its definition but deliberately
    // preserves whether it is enabled, so redeploying does not silently resume
    // something an operator paused. The next fire time is recomputed only when
    // the spec itself changed, so a redeploy does not skip or repeat an
    // occurrence either.
    upsert(input: ScheduleUpsert, nowMs: number): Promise<ScheduleRecord>;

    // Enabled schedules whose next occurrence has arrived. Not leased: the run
    // insert that follows is keyed by occurrence, so two workers materializing
    // the same one is harmless.
    listDue(nowMs: number, limit: number): Promise<ScheduleRecord[]>;

    list(): Promise<ScheduleRecord[]>;

    get(name: string): Promise<ScheduleRecord | null>;

    advance(
        name: string, nextFireAtMs: number | null, lastFireAtMs: number, nowMs: number
    ): Promise<void>;

    // Pause and resume. Returns false when there is no such schedule.
    setEnabled(name: string, enabled: boolean, nowMs: number): Promise<boolean>;

    remove(name: string): Promise<boolean>;
}
