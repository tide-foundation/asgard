// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Text.Json;

namespace Tide.Asgard.Scheduler.Execution;

// Reference implementation and test double. Everything survives only as long as
// the process, so use it for local timers and for exercising a worker, not for
// work that must not be lost.
//
// A worker dispatches concurrently, so unlike the single threaded TypeScript
// implementation this one has to lock. The lock stands in for the transaction a
// durable store would use, and no method awaits while holding it.
public sealed class InMemoryJobStore : IJobStore
{
	private readonly Lock _gate = new();
	private readonly Dictionary<string, JobRun> _runs = [];
	private readonly HashSet<string> _keys = [];
	private int _sequence;

	public Task<JobRun?> EnqueueAsync(JobRunRequest request, CancellationToken ct = default)
	{
		lock (_gate)
		{
			return Task.FromResult(EnqueueLocked(request));
		}
	}

	public Task<IReadOnlyList<JobRun>> ClaimDueAsync(
		string owner, long nowMs, long leaseMs, int max,
		IReadOnlyCollection<string>? handlers = null, CancellationToken ct = default)
	{
		var allowed = handlers is null ? null : new HashSet<string>(handlers);

		lock (_gate)
		{
			var due = _runs.Values
				.Where(r => r.Status == JobStatus.Pending && r.RunAtMs <= nowMs)
				.Where(r => allowed is null || allowed.Contains(r.Handler))
				.OrderBy(r => r.RunAtMs)
				.ThenBy(r => SequenceOf(r.Id))
				.Take(Math.Max(0, max))
				.ToList();

			var claimed = new List<JobRun>(due.Count);
			foreach (var run in due)
			{
				var updated = run with
				{
					Status = JobStatus.Leased,
					Attempt = run.Attempt + 1,
					LeaseOwner = owner,
					LeaseExpiresAtMs = nowMs + leaseMs,
					UpdatedAtMs = nowMs
				};
				_runs[run.Id] = updated;
				claimed.Add(updated);
			}

			return Task.FromResult<IReadOnlyList<JobRun>>(claimed);
		}
	}

	public Task<bool> HeartbeatAsync(string runId, long leaseUntilMs, CancellationToken ct = default)
	{
		lock (_gate)
		{
			if (!_runs.TryGetValue(runId, out var run) || run.Status != JobStatus.Leased)
			{
				return Task.FromResult(false);
			}

			_runs[runId] = run with { LeaseExpiresAtMs = leaseUntilMs, UpdatedAtMs = leaseUntilMs };
			return Task.FromResult(true);
		}
	}

	public Task CompleteAsync(
		string runId, JobRunRequest? next, long nowMs, CancellationToken ct = default)
	{
		lock (_gate)
		{
			if (_runs.TryGetValue(runId, out var run))
			{
				_runs[runId] = run with
				{
					Status = JobStatus.Succeeded,
					LeaseOwner = null,
					LeaseExpiresAtMs = null,
					UpdatedAtMs = nowMs
				};

				// Same lock, so a caller can never settle without chaining.
				if (next is not null) EnqueueLocked(next);
			}

			return Task.CompletedTask;
		}
	}

	public Task RetryAsync(
		string runId, string error, long runAtMs, long nowMs, CancellationToken ct = default)
	{
		lock (_gate)
		{
			if (_runs.TryGetValue(runId, out var run))
			{
				_runs[runId] = run with
				{
					Status = JobStatus.Pending,
					RunAtMs = runAtMs,
					LastError = error,
					LeaseOwner = null,
					LeaseExpiresAtMs = null,
					UpdatedAtMs = nowMs
				};
			}

			return Task.CompletedTask;
		}
	}

	public Task DeadLetterAsync(
		string runId, string error, JobRunRequest? next, long nowMs, CancellationToken ct = default)
	{
		lock (_gate)
		{
			if (_runs.TryGetValue(runId, out var run))
			{
				_runs[runId] = run with
				{
					Status = JobStatus.Dead,
					LastError = error,
					LeaseOwner = null,
					LeaseExpiresAtMs = null,
					UpdatedAtMs = nowMs
				};

				if (next is not null) EnqueueLocked(next);
			}

			return Task.CompletedTask;
		}
	}

	public Task<int> ReapExpiredAsync(long nowMs, CancellationToken ct = default)
	{
		lock (_gate)
		{
			var reaped = 0;

			foreach (var run in _runs.Values.ToList())
			{
				if (run.Status != JobStatus.Leased) continue;
				if (run.LeaseExpiresAtMs is { } expires && expires > nowMs) continue;

				_runs[run.Id] = run with
				{
					Status = JobStatus.Pending,
					LeaseOwner = null,
					LeaseExpiresAtMs = null,
					LastError = "lease expired",
					UpdatedAtMs = nowMs
				};
				reaped++;
			}

			return Task.FromResult(reaped);
		}
	}

	public Task<int> PurgeSettledAsync(
		long beforeMs, int limit, bool includeDead = false, CancellationToken ct = default)
	{
		lock (_gate)
		{
			var victims = _runs.Values
				.Where(r => r.Status == JobStatus.Succeeded
					|| (includeDead && r.Status == JobStatus.Dead))
				.Where(r => r.UpdatedAtMs < beforeMs)
				.OrderBy(r => r.UpdatedAtMs)
				.ThenBy(r => SequenceOf(r.Id))
				.Take(Math.Max(0, limit))
				.ToList();

			foreach (var run in victims)
			{
				_runs.Remove(run.Id);

				// The key goes with the row, matching a delete in a durable
				// store. A schedule that later re-materializes the same
				// occurrence is then free to enqueue it again, which is why
				// retention must outlive the period of anything it holds keys for.
				if (run.IdempotencyKey is { } key) _keys.Remove(key);
			}

			return Task.FromResult(victims.Count);
		}
	}

	public Task<JobStoreStats> StatsAsync(long nowMs, CancellationToken ct = default)
	{
		lock (_gate)
		{
			long oldest = 0;
			foreach (var run in _runs.Values)
			{
				if (run.Status != JobStatus.Pending || run.RunAtMs > nowMs) continue;
				oldest = Math.Max(oldest, nowMs - run.RunAtMs);
			}

			return Task.FromResult(new JobStoreStats(
				_runs.Values.Count(r => r.Status == JobStatus.Pending),
				_runs.Values.Count(r => r.Status == JobStatus.Leased),
				_runs.Values.Count(r => r.Status == JobStatus.Succeeded),
				_runs.Values.Count(r => r.Status == JobStatus.Dead),
				oldest));
		}
	}

	public Task<JobRun?> GetAsync(string runId, CancellationToken ct = default)
	{
		lock (_gate)
		{
			return Task.FromResult(_runs.GetValueOrDefault(runId));
		}
	}

	// Inspection helpers for tests and local debugging. Not part of IJobStore.

	public IReadOnlyList<JobRun> All()
	{
		lock (_gate) return _runs.Values.OrderBy(r => SequenceOf(r.Id)).ToList();
	}

	public IReadOnlyList<JobRun> ByStatus(JobStatus status)
		=> All().Where(r => r.Status == status).ToList();

	public int CountByStatus(JobStatus status) => ByStatus(status).Count;

	private JobRun? EnqueueLocked(JobRunRequest request)
	{
		if (request.IdempotencyKey is { } key && _keys.Contains(key)) return null;

		// Ids are sequential rather than random so test failures are readable.
		_sequence++;
		var run = new JobRun
		{
			Id = $"run-{_sequence}",
			ScheduleId = request.ScheduleId,
			Handler = request.Handler,
			Payload = NormalizePayload(request.Payload),
			IdempotencyKey = request.IdempotencyKey,
			RunAtMs = request.RunAtMs,
			Status = JobStatus.Pending,
			Attempt = 0,
			MaxAttempts = request.MaxAttempts,
			CreatedAtMs = request.RunAtMs,
			UpdatedAtMs = request.RunAtMs
		};

		_runs[run.Id] = run;
		if (request.IdempotencyKey is { } added) _keys.Add(added);
		return run;
	}

	// Sequential ids compared numerically so run-10 sorts after run-9.
	private static int SequenceOf(string id) => int.Parse(id.AsSpan(4));

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	// Payloads go through JSON here even though nothing forces it, so that a
	// handler sees exactly what it would see through a durable store. Without
	// this a job would work in tests and then meet a DateTime turned into a
	// string the first time it ran against Postgres.
	private static object? NormalizePayload(object? payload)
		=> payload is null ? null : JsonSerializer.SerializeToNode(payload, JsonOptions);
}
