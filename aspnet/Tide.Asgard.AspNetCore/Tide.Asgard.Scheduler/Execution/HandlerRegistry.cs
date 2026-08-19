// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Execution;

public sealed class JobContext
{
	public required string RunId { get; init; }

	// Counts from 1. Useful for logging and for handlers that behave differently
	// on a retry.
	public required int Attempt { get; init; }
	public required int MaxAttempts { get; init; }

	// Cancelled on worker shutdown.
	public required CancellationToken CancellationToken { get; init; }

	// Extends the lease. A worker started with Start renews leases for you, so
	// this is only needed when driving TickAsync directly, or to check whether
	// the lease is still held. Returns false when it is not, at which point the
	// handler should stop because another worker has taken the run.
	public required Func<Task<bool>> Heartbeat { get; init; }
}

// Jobs are looked up by name at execution time rather than captured as closures,
// because a durable store holds a name and a payload, not a function. That is
// also what lets a run enqueued by one process execute in another.
public sealed class HandlerRegistry
{
	private readonly Dictionary<string, JobDefinition> _jobs = [];

	public HandlerRegistry Register(JobDefinition definition)
	{
		if (!_jobs.TryAdd(definition.Name, definition))
		{
			throw new InvalidOperationException($"job '{definition.Name}' is already registered");
		}
		return this;
	}

	public HandlerRegistry RegisterAll(IEnumerable<JobDefinition> definitions)
	{
		foreach (var definition in definitions) Register(definition);
		return this;
	}

	public JobDefinition? Resolve(string name) => _jobs.GetValueOrDefault(name);

	public bool Has(string name) => _jobs.ContainsKey(name);

	public IReadOnlyList<string> Names() => _jobs.Keys.ToList();

	public IReadOnlyList<JobDefinition> Definitions() => _jobs.Values.ToList();
}
