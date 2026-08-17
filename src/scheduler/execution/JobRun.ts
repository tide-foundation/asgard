// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

export enum JobStatus {
    // Waiting for runAtMs to arrive. Retries return here rather than creating a
    // new row, so attempt survives across attempts.
    Pending = "pending",
    // Claimed by a worker and running. Reverts to Pending if the lease expires.
    Leased = "leased",
    Succeeded = "succeeded",
    // Terminal failure: attempts exhausted, or the handler raised
    // PermanentJobError.
    Dead = "dead"
}

export interface JobRun {
    readonly id: string;
    // Set when this run was materialized from a recurring schedule.
    readonly scheduleId: string | null;
    readonly handler: string;
    readonly payload: unknown;
    // Unique across the store. Two workers materializing the same occurrence
    // both try to insert it and exactly one wins, which is what removes the
    // need for leader election.
    readonly idempotencyKey: string | null;
    readonly runAtMs: number;
    readonly status: JobStatus;
    // Incremented when the run is claimed, so it counts attempts started.
    readonly attempt: number;
    readonly maxAttempts: number;
    readonly leaseOwner: string | null;
    readonly leaseExpiresAtMs: number | null;
    readonly lastError: string | null;
    readonly createdAtMs: number;
    readonly updatedAtMs: number;
}

export interface JobRunRequest {
    readonly handler: string;
    readonly payload?: unknown;
    readonly runAtMs: number;
    readonly scheduleId?: string | null;
    readonly idempotencyKey?: string | null;
    readonly maxAttempts?: number;
}
