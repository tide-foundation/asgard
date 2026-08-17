// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Tide.Asgard.Scheduler.Expression;

namespace Tide.Asgard.Scheduler.Execution;

public sealed record ScheduleRecord
{
	public required string Name { get; init; }
	public required string Handler { get; init; }
	public object? Payload { get; init; }

	// Kept for display and for admin listings. The spec is what actually runs.
	public required string Expr { get; init; }
	public required ScheduleSpec Spec { get; init; }

	public bool Enabled { get; init; } = true;
	public MisfirePolicy Misfire { get; init; } = MisfirePolicy.FireOnce;
	public int? MaxAttempts { get; init; }

	// Null when the next occurrence is chained on settle rather than
	// materialized, or when the schedule can never fire again.
	public long? NextFireAtMs { get; init; }
	public long? LastFireAtMs { get; init; }
	public long UpdatedAtMs { get; init; }
}

public sealed record ScheduleUpsert
{
	public required string Name { get; init; }
	public required string Handler { get; init; }
	public object? Payload { get; init; }
	public required string Expr { get; init; }
	public required ScheduleSpec Spec { get; init; }
	public MisfirePolicy Misfire { get; init; } = MisfirePolicy.FireOnce;
	public int? MaxAttempts { get; init; }

	// Used only when the schedule is new, or when its spec has changed.
	public long? NextFireAtMs { get; init; }
}

// Implemented by schedule stores that own their tables.
public interface ISchemaAwareScheduleStore
{
	Task EnsureSchemaAsync(CancellationToken ct = default);
}

// Where recurring schedules live. Separate from IJobStore because a schedule is
// a definition while a run is an occurrence, and because a host may reasonably
// want durable runs with schedules still declared in code.
public interface IScheduleStore
{
	// Registering an existing schedule updates its definition but deliberately
	// preserves whether it is enabled, so redeploying does not silently resume
	// something an operator paused. The next fire time is recomputed only when
	// the spec itself changed, so a redeploy does not skip or repeat an
	// occurrence either.
	Task<ScheduleRecord> UpsertAsync(ScheduleUpsert input, long nowMs, CancellationToken ct = default);

	// Enabled schedules whose next occurrence has arrived. Not leased: the run
	// insert that follows is keyed by occurrence, so two workers materializing
	// the same one is harmless.
	Task<IReadOnlyList<ScheduleRecord>> ListDueAsync(
		long nowMs, int limit, CancellationToken ct = default);

	Task<IReadOnlyList<ScheduleRecord>> ListAsync(CancellationToken ct = default);

	Task<ScheduleRecord?> GetAsync(string name, CancellationToken ct = default);

	Task AdvanceAsync(
		string name, long? nextFireAtMs, long lastFireAtMs, long nowMs, CancellationToken ct = default);

	// Pause and resume. Returns false when there is no such schedule.
	Task<bool> SetEnabledAsync(string name, bool enabled, long nowMs, CancellationToken ct = default);

	Task<bool> RemoveAsync(string name, CancellationToken ct = default);
}
