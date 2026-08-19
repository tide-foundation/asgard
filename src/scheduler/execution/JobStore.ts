// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { JobRun, JobRunRequest } from "./JobRun";

export interface JobStoreStats {
    readonly pending: number;
    readonly leased: number;
    readonly succeeded: number;
    readonly dead: number;
    readonly cancelled: number;
    // Age of the oldest run that is due and still waiting. This is the number
    // worth alerting on: queue depth lies, because a large batch looks exactly
    // like an outage, whereas a rising oldest age only ever means work is not
    // being picked up.
    readonly oldestPendingAgeMs: number;
}

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
    //
    // When handlers is given, only runs for those handler names are claimed. A
    // worker passes what it has registered, so in a mixed fleet a run is left
    // for a process that can actually execute it rather than being claimed and
    // failed by one that cannot.
    claimDue(
        owner: string,
        nowMs: number,
        leaseMs: number,
        max: number,
        handlers?: readonly string[]
    ): Promise<JobRun[]>;

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

    // Deletes settled runs that finished before beforeMs, at most limit at a
    // time so a long backlog is cleared in bounded chunks rather than one
    // statement holding a lock over the whole table. Succeeded runs only by
    // default: a dead run is evidence, and deleting it silently loses the only
    // record that something never ran.
    purgeSettled(beforeMs: number, limit: number, includeDead?: boolean): Promise<number>;

    // Admin. Stops a run that has not finished. Returns false when it is
    // already settled, which is the honest answer to cancelling something that
    // has already happened.
    cancel(runId: string, nowMs: number): Promise<boolean>;

    // Admin. Puts a dead or cancelled run back in the queue with a fresh set of
    // attempts. Returns false when the run is not in a state that can be
    // requeued.
    requeue(runId: string, runAtMs: number, nowMs: number): Promise<boolean>;

    stats(nowMs: number): Promise<JobStoreStats>;

    get(runId: string): Promise<JobRun | null>;
}
