// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Execution;

public enum JobStatus
{
	// Waiting for RunAtMs to arrive. Retries return here rather than creating a
	// new row, so Attempt survives across attempts.
	Pending,
	// Claimed by a worker and running. Reverts to Pending if the lease expires.
	Leased,
	Succeeded,
	// Terminal failure: attempts exhausted, or the handler raised
	// PermanentJobException.
	Dead,
	// Stopped by an operator before it ran. Kept rather than deleted so the
	// record of the decision survives.
	Cancelled
}

public sealed record JobRun
{
	public required string Id { get; init; }

	// Set when this run was materialized from a recurring schedule.
	public string? ScheduleId { get; init; }

	public required string Handler { get; init; }
	public object? Payload { get; init; }

	// Unique across the store. Two workers materializing the same occurrence
	// both try to insert it and exactly one wins, which is what removes the need
	// for leader election.
	public string? IdempotencyKey { get; init; }

	public required long RunAtMs { get; init; }
	public required JobStatus Status { get; init; }

	// Incremented when the run is claimed, so it counts attempts started.
	public int Attempt { get; init; }
	public int MaxAttempts { get; init; } = 1;

	public string? LeaseOwner { get; init; }
	public long? LeaseExpiresAtMs { get; init; }
	public string? LastError { get; init; }
	public long CreatedAtMs { get; init; }
	public long UpdatedAtMs { get; init; }
}

public sealed record JobRunRequest
{
	public required string Handler { get; init; }
	public object? Payload { get; init; }
	public required long RunAtMs { get; init; }
	public string? ScheduleId { get; init; }
	public string? IdempotencyKey { get; init; }
	public int MaxAttempts { get; init; } = 1;
}
