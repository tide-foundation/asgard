// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Execution;

// The seam between the scheduler and whatever database the host already uses.
// Implementing this is the only work needed to make the scheduler durable, and
// it is why the scheduler itself has no database dependency.
//
// The execution contract is at-least-once. A worker can die after a side effect
// and before its settle call, so handlers must be idempotent. No store can fix
// that, it is a property of running work outside the transaction that records it.
public interface IJobStore
{
	// Returns null when IdempotencyKey is already present, which is how repeat
	// materialization of the same occurrence is discarded.
	Task<JobRun?> EnqueueAsync(JobRunRequest request, CancellationToken ct = default);

	// Atomically hands at most max due runs to one owner and extends their
	// leases. A durable implementation does this in a single statement, for
	// Postgres an UPDATE over a SELECT ... FOR UPDATE SKIP LOCKED, so that
	// concurrent workers never claim the same run.
	Task<IReadOnlyList<JobRun>> ClaimDueAsync(
		string owner, long nowMs, long leaseMs, int max, CancellationToken ct = default);

	// Extends a lease while a handler is still working. Returns false when the
	// lease was already lost, which means the reaper has handed the run to
	// someone else and this worker should stop.
	Task<bool> HeartbeatAsync(string runId, long leaseUntilMs, CancellationToken ct = default);

	// Settles a run as succeeded and enqueues its successor in the same commit.
	// Splitting these is the classic way to lose a recurring schedule forever:
	// a crash in between leaves nothing scheduled and nothing to notice.
	Task CompleteAsync(string runId, JobRunRequest? next, long nowMs, CancellationToken ct = default);

	// Returns a run to Pending with a later RunAtMs after a failed attempt.
	Task RetryAsync(string runId, string error, long runAtMs, long nowMs, CancellationToken ct = default);

	// Terminal failure. Takes a successor for the same reason CompleteAsync does:
	// a recurring schedule must survive a run that could not be salvaged, or one
	// bad night stops the job forever.
	Task DeadLetterAsync(
		string runId, string error, JobRunRequest? next, long nowMs, CancellationToken ct = default);

	// Returns leased runs whose lease has expired to Pending. This is what makes
	// a crashed worker recoverable, and also what makes double execution
	// possible, hence the at-least-once contract.
	Task<int> ReapExpiredAsync(long nowMs, CancellationToken ct = default);

	Task<JobRun?> GetAsync(string runId, CancellationToken ct = default);
}
