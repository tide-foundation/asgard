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

	// Extends the lease. A handler that may outlive the lease has to call this
	// periodically, otherwise the reaper will hand its run to another worker
	// while it is still running. Returns false when the lease is already gone,
	// at which point the handler should stop.
	public required Func<Task<bool>> Heartbeat { get; init; }
}

public delegate Task JobHandler(object? payload, JobContext ctx);

// Handlers are looked up by name at execution time rather than captured as
// closures, because a durable store holds a name and a payload, not a function.
// That is also what lets a run enqueued by one process execute in another.
public sealed class HandlerRegistry
{
	private readonly Dictionary<string, JobHandler> _handlers = [];

	public HandlerRegistry Register(string name, JobHandler handler)
	{
		if (!_handlers.TryAdd(name, handler))
		{
			throw new InvalidOperationException($"handler '{name}' is already registered");
		}
		return this;
	}

	// Convenience overload for handlers that do not need to await anything.
	public HandlerRegistry Register(string name, Action<object?, JobContext> handler)
		=> Register(name, (payload, ctx) =>
		{
			handler(payload, ctx);
			return Task.CompletedTask;
		});

	public JobHandler? Resolve(string name) => _handlers.GetValueOrDefault(name);

	public bool Has(string name) => _handlers.ContainsKey(name);

	public IReadOnlyList<string> Names() => _handlers.Keys.ToList();
}
