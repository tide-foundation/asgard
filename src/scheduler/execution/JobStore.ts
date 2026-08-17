// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { JobRun, JobRunRequest } from "./JobRun";

// The seam between the scheduler and whatever database the host already uses.
// Implementing this is the only work needed to make the scheduler durable, and
// it is why the scheduler itself has no database dependency.
//
// The execution contract is at-least-once. A worker can die after a side effect
// and before its settle call, so handlers must be idempotent. No store can fix
// that, it is a property of running work outside the transaction that records it.
export interface JobStore {
    // Returns null when idempotencyKey is already present, which is how repeat
    // materialization of the same occurrence is discarded.
    enqueue(request: JobRunRequest): Promise<JobRun | null>;

    // Atomically hands at most max due runs to one owner and extends their
    // leases. A durable implementation does this in a single statement, for
    // Postgres an UPDATE over a SELECT ... FOR UPDATE SKIP LOCKED, so that
    // concurrent workers never claim the same run.
    claimDue(owner: string, nowMs: number, leaseMs: number, max: number): Promise<JobRun[]>;

    // Extends a lease while a handler is still working. Returns false when the
    // lease was already lost, which means the reaper has handed the run to
    // someone else and this worker should stop.
    heartbeat(runId: string, leaseUntilMs: number): Promise<boolean>;

    // Settles a run as succeeded and enqueues its successor in the same commit.
    // Splitting these is the classic way to lose a recurring schedule forever:
    // a crash in between leaves nothing scheduled and nothing to notice.
    complete(runId: string, next: JobRunRequest | null, nowMs: number): Promise<void>;

    // Returns a run to Pending with a later runAtMs after a failed attempt.
    retry(runId: string, error: string, runAtMs: number, nowMs: number): Promise<void>;

    // Terminal failure. Takes a successor for the same reason complete does: a
    // recurring schedule must survive a run that could not be salvaged, or one
    // bad night stops the job forever.
    deadLetter(runId: string, error: string, next: JobRunRequest | null, nowMs: number): Promise<void>;

    // Returns leased runs whose lease has expired to Pending. This is what makes
    // a crashed worker recoverable, and also what makes double execution
    // possible, hence the at-least-once contract.
    reapExpired(nowMs: number): Promise<number>;

    get(runId: string): Promise<JobRun | null>;
}
