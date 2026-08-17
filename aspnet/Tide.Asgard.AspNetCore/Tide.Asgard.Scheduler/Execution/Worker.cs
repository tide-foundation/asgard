// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Tide.Asgard.Scheduler.Expression;

namespace Tide.Asgard.Scheduler.Execution;

public enum MisfirePolicy
{
	// Catch up with a single run, whatever was missed. The right default: after
	// an outage you usually want the job to happen, once, not sixty times.
	FireOnce,
	// Enqueue every missed occurrence.
	FireAll,
	// Abandon what was missed and wait for the next occurrence.
	Skip
}

public sealed record ScheduleDefinition
{
	// Unique. Also forms the idempotency key of every run it materializes, so
	// two workers materializing the same occurrence produce the same key and
	// exactly one insert survives.
	public required string Name { get; init; }
	public required string Expr { get; init; }

	// The job to run. Prefer the For factory below, which checks the payload
	// against the job rather than taking it as a bare object.
	public required JobDefinition Job { get; init; }
	public object? Payload { get; init; }
	public int? MaxAttempts { get; init; }
	public MisfirePolicy Misfire { get; init; } = MisfirePolicy.FireOnce;

	// Type checked construction. The record itself cannot be generic because a
	// worker holds a list of schedules with different payload types.
	public static ScheduleDefinition For<TPayload>(
		string name,
		string expr,
		JobDefinition<TPayload> job,
		TPayload payload,
		int? maxAttempts = null,
		MisfirePolicy misfire = MisfirePolicy.FireOnce)
		=> new()
		{
			Name = name,
			Expr = expr,
			Job = job,
			Payload = payload,
			MaxAttempts = maxAttempts,
			Misfire = misfire
		};

	public static ScheduleDefinition For(
		string name,
		string expr,
		JobDefinition job,
		int? maxAttempts = null,
		MisfirePolicy misfire = MisfirePolicy.FireOnce)
		=> new()
		{
			Name = name,
			Expr = expr,
			Job = job,
			MaxAttempts = maxAttempts,
			Misfire = misfire
		};
}

public sealed record RetentionPolicy
{
	// Settled runs older than this are deleted.
	public required long AfterMs { get; init; }
	// How often to sweep.
	public required long EveryMs { get; init; }
	// Rows per sweep, so a long backlog clears over several passes.
	public int Batch { get; init; } = 1_000;
	// Off by default. A dead run is evidence that something never ran, and
	// deleting it loses the only record of that.
	public bool IncludeDead { get; init; }
}

public sealed record WorkerOptions
{
	public required IJobStore Store { get; init; }

	// Jobs to register. Either give these and let the worker build a registry,
	// or bring your own registry, or both.
	public IReadOnlyList<JobDefinition>? Jobs { get; init; }
	public HandlerRegistry? Registry { get; init; }

	// Recurring schedules to register up front. Registering touches the schedule
	// store, so these are applied by CreateAsync rather than the constructor.
	public IReadOnlyList<ScheduleDefinition>? Schedules { get; init; }

	// Where schedules live. Defaults to memory, which is right when schedules are
	// declared in code. Give it a PostgresScheduleStore to have a pause survive a
	// restart, or to add a schedule without a deploy.
	public IScheduleStore? ScheduleStore { get; init; }

	public IClock Clock { get; init; } = SystemClock.Instance;

	// Identifies this worker in lease records. Defaults to a random label.
	public string? Owner { get; init; }

	public int Concurrency { get; init; } = 4;
	public long PollIntervalMs { get; init; } = 1_000;
	public long LeaseMs { get; init; } = 30_000;

	// How often Start renews the leases of in flight runs. Defaults to a third of
	// LeaseMs, so two renewals can be missed before a lease lapses.
	public long? HeartbeatMs { get; init; }

	// Claim only runs whose handler is registered here. On by default, so in a
	// mixed fleet a run is left for a process that can execute it. Turn off to
	// have this worker claim everything and dead letter what it cannot run.
	public bool ClaimOnlyRegisteredHandlers { get; init; } = true;

	public RetentionPolicy? Retention { get; init; }
	public RetryPolicy Retry { get; init; } = RetryPolicy.Default;
	public Func<double>? Random { get; init; }
	public Action<Exception, JobRun?>? OnError { get; init; }
}

public sealed record TickResult(
	int Reaped, int Materialized, int Claimed, int Succeeded, int Retried, int Dead, int Purged);

public sealed class Worker : IAsyncDisposable
{
	// Guards against enumerating an unbounded number of missed occurrences when
	// a fast schedule has been down for a long time.
	private const int MaxCatchUp = 10_000;

	// A tick materializes at most this many schedules, so one pass stays bounded
	// no matter how many are registered.
	private const int MaxSchedulesPerTick = 1_000;

	private readonly IJobStore _store;
	private readonly HandlerRegistry _registry;
	private readonly IClock _clock;
	private readonly string _owner;
	private readonly int _concurrency;
	private readonly long _pollIntervalMs;
	private readonly long _leaseMs;
	private readonly long _heartbeatMs;
	private readonly bool _claimOnlyRegisteredHandlers;
	private readonly RetentionPolicy? _retention;
	private readonly RetryPolicy _retry;
	private readonly Func<double> _random;
	private readonly Action<Exception, JobRun?> _onError;

	private readonly IScheduleStore _scheduleStore;
	private readonly HashSet<string> _inFlight = [];
	private readonly Lock _inFlightGate = new();
	private readonly CancellationTokenSource _stop = new();
	private Task? _loop;
	private Task? _renewal;
	private long _nextPurgeAtMs;

	// Everything needed to go from nothing to a running scheduler in one call:
	//
	//   await using var scheduler = await Worker.CreateAsync(new WorkerOptions
	//   {
	//       Store = PostgresJobStore.Create(connectionString),
	//       Jobs = [reconcileOrks],
	//       Schedules = [ScheduleDefinition.For("nightly", "on 03:00", reconcileOrks, payload)]
	//   });
	//   scheduler.Start();
	//
	// The only thing this adds over the constructor is applying the store's
	// schema when it has one, which is the step most easily forgotten and the one
	// whose absence fails at the least convenient moment.
	public static async Task<Worker> CreateAsync(WorkerOptions options, CancellationToken ct = default)
	{
		if (options.Store is ISchemaAwareJobStore store) await store.EnsureSchemaAsync(ct);
		if (options.ScheduleStore is ISchemaAwareScheduleStore schedules)
		{
			await schedules.EnsureSchemaAsync(ct);
		}

		var worker = new Worker(options);

		// Registering a schedule touches the schedule store, which is why
		// schedules are applied here rather than in the constructor.
		foreach (var schedule in options.Schedules ?? []) await worker.AddScheduleAsync(schedule, ct);

		return worker;
	}

	public Worker(WorkerOptions options)
	{
		_store = options.Store;
		_registry = options.Registry ?? new HandlerRegistry();
		_scheduleStore = options.ScheduleStore ?? new InMemoryScheduleStore();
		if (options.Jobs is not null) _registry.RegisterAll(options.Jobs);
		_clock = options.Clock;
		_owner = options.Owner ?? $"worker-{Guid.NewGuid():N}"[..16];
		_concurrency = Math.Max(1, options.Concurrency);
		_pollIntervalMs = Math.Max(1, options.PollIntervalMs);
		_leaseMs = Math.Max(1, options.LeaseMs);
		_heartbeatMs = Math.Max(1, options.HeartbeatMs ?? _leaseMs / 3);
		_claimOnlyRegisteredHandlers = options.ClaimOnlyRegisteredHandlers;
		_retention = options.Retention;
		_retry = options.Retry;
		_random = options.Random ?? System.Random.Shared.NextDouble;
		_onError = options.OnError ?? ((_, _) => { });
	}

	// Registers a recurring schedule, or updates one that already exists.
	// Re-registering keeps whether it is enabled, so a redeploy cannot silently
	// resume something an operator paused, and keeps its place in time unless the
	// expression itself changed.
	public Task<ScheduleRecord> AddScheduleAsync(
		ScheduleDefinition definition, CancellationToken ct = default)
	{
		if (!_registry.Has(definition.Job.Name)) _registry.Register(definition.Job);

		var spec = ScheduleParser.Parse(definition.Expr);
		var now = _clock.NowMs;

		return _scheduleStore.UpsertAsync(new ScheduleUpsert
		{
			Name = definition.Name,
			Handler = definition.Job.Name,
			Payload = definition.Payload,
			Expr = definition.Expr,
			Spec = spec,
			Misfire = definition.Misfire,
			MaxAttempts = definition.MaxAttempts ?? definition.Job.MaxAttempts,
			NextFireAtMs = ScheduleEvaluator.NextFire(spec, now)
		}, now, ct);
	}

	// Admin surface.

	public Task<IReadOnlyList<ScheduleRecord>> ListSchedulesAsync(CancellationToken ct = default)
		=> _scheduleStore.ListAsync(ct);

	public Task<ScheduleRecord?> GetScheduleAsync(string name, CancellationToken ct = default)
		=> _scheduleStore.GetAsync(name, ct);

	public Task<bool> PauseScheduleAsync(string name, CancellationToken ct = default)
		=> _scheduleStore.SetEnabledAsync(name, false, _clock.NowMs, ct);

	public Task<bool> ResumeScheduleAsync(string name, CancellationToken ct = default)
		=> _scheduleStore.SetEnabledAsync(name, true, _clock.NowMs, ct);

	public Task<bool> RemoveScheduleAsync(string name, CancellationToken ct = default)
		=> _scheduleStore.RemoveAsync(name, ct);

	// Runs a schedule now without disturbing its timetable. The key is distinct
	// from a materialized occurrence, so triggering twice in the same millisecond
	// is the only way to collide, and a paused schedule can still be triggered on
	// purpose.
	public async Task<JobRun?> TriggerScheduleAsync(string name, CancellationToken ct = default)
	{
		var record = await _scheduleStore.GetAsync(name, ct);
		if (record is null) return null;

		var now = _clock.NowMs;
		return await _store.EnqueueAsync(new JobRunRequest
		{
			Handler = record.Handler,
			Payload = record.Payload,
			RunAtMs = now,
			ScheduleId = record.Name,
			IdempotencyKey = $"{record.Name}:manual:{now}",
			MaxAttempts = record.MaxAttempts ?? _retry.MaxAttempts
		}, ct);
	}

	public Task<bool> CancelRunAsync(string runId, CancellationToken ct = default)
		=> _store.CancelAsync(runId, _clock.NowMs, ct);

	public Task<bool> RequeueRunAsync(
		string runId, long? runAtMs = null, CancellationToken ct = default)
	{
		var now = _clock.NowMs;
		return _store.RequeueAsync(runId, runAtMs ?? now, now, ct);
	}

	// Queues a one off job. Pass the definition, not its name, so the payload is
	// checked against the handler that will receive it.
	public Task<JobRun?> EnqueueAsync<TPayload>(
		JobDefinition<TPayload> job,
		TPayload payload,
		long? runAtMs = null,
		int? maxAttempts = null,
		string? idempotencyKey = null,
		CancellationToken ct = default)
	{
		if (!_registry.Has(job.Name)) _registry.Register(job);
		return EnqueueByNameAsync(
			job.Name, payload, runAtMs, maxAttempts ?? job.MaxAttempts, idempotencyKey, ct);
	}

	// Queues a job that carries no payload.
	public Task<JobRun?> EnqueueAsync(
		JobDefinition job,
		long? runAtMs = null,
		int? maxAttempts = null,
		string? idempotencyKey = null,
		CancellationToken ct = default)
	{
		if (!_registry.Has(job.Name)) _registry.Register(job);
		return EnqueueByNameAsync(
			job.Name, null, runAtMs, maxAttempts ?? job.MaxAttempts, idempotencyKey, ct);
	}

	// Escape hatch for queueing a job whose definition is not to hand, for
	// example from an admin endpoint that takes a name off a request.
	public Task<JobRun?> EnqueueByNameAsync(
		string handler,
		object? payload = null,
		long? runAtMs = null,
		int? maxAttempts = null,
		string? idempotencyKey = null,
		CancellationToken ct = default)
		=> _store.EnqueueAsync(new JobRunRequest
		{
			Handler = handler,
			Payload = payload,
			RunAtMs = runAtMs ?? _clock.NowMs,
			MaxAttempts = maxAttempts ?? _retry.MaxAttempts,
			IdempotencyKey = idempotencyKey
		}, ct);

	// One pass of the loop. Exposed so tests can drive the worker with a fake
	// clock and assert on each step instead of racing real timers.
	public async Task<TickResult> TickAsync(CancellationToken ct = default)
	{
		var now = _clock.NowMs;

		var reaped = await _store.ReapExpiredAsync(now, ct);
		var purged = await PurgeAsync(now, ct);
		var materialized = await MaterializeAsync(now, ct);

		var claimed = await _store.ClaimDueAsync(
			_owner, now, _leaseMs, _concurrency,
			_claimOnlyRegisteredHandlers ? _registry.Names() : null, ct);

		var outcomes = await Task.WhenAll(claimed.Select(run => DispatchAsync(run, ct)));

		return new TickResult(
			reaped,
			materialized,
			claimed.Count,
			outcomes.Count(o => o == Outcome.Succeeded),
			outcomes.Count(o => o == Outcome.Retried),
			outcomes.Count(o => o == Outcome.Dead),
			purged);
	}

	// Counts by status plus the age of the oldest waiting run, for feeding
	// metrics. Alert on OldestPendingAgeMs.
	public Task<JobStoreStats> StatsAsync(CancellationToken ct = default)
		=> _store.StatsAsync(_clock.NowMs, ct);

	public void Start()
	{
		if (_loop is not null) return;

		_loop = Task.Run(async () =>
		{
			while (!_stop.IsCancellationRequested)
			{
				try
				{
					await TickAsync(_stop.Token);
				}
				catch (Exception e)
				{
					_onError(e, null);
				}
				await _clock.Delay(_pollIntervalMs, _stop.Token);
			}
		});

		_renewal = Task.Run(RenewLeasesAsync);
	}

	// Stops claiming and waits for the current pass to finish. In flight handlers
	// see their token cancel. Anything still leased is left to the reaper, which
	// is why handlers have to be idempotent.
	public async Task StopAsync()
	{
		await _stop.CancelAsync();

		var loop = _loop;
		var renewal = _renewal;
		_loop = null;
		_renewal = null;

		if (loop is not null) await loop;
		if (renewal is not null) await renewal;
	}

	// Keeps the leases of in flight runs alive for as long as their handlers are
	// working, so a handler that outlives LeaseMs is not reaped and run twice by
	// another worker. Runs alongside the tick loop rather than inside it, because
	// a tick is blocked on the very handlers that need renewing.
	private async Task RenewLeasesAsync()
	{
		while (!_stop.IsCancellationRequested)
		{
			await _clock.Delay(_heartbeatMs, _stop.Token);
			if (_stop.IsCancellationRequested) break;

			List<string> runIds;
			lock (_inFlightGate) runIds = [.. _inFlight];
			if (runIds.Count == 0) continue;

			var until = _clock.NowMs + _leaseMs;
			foreach (var runId in runIds)
			{
				try
				{
					// A lost lease means the reaper already gave the run to
					// someone else. Stop renewing, the handler's own heartbeat
					// call will tell it to stop.
					if (!await _store.HeartbeatAsync(runId, until, _stop.Token))
					{
						lock (_inFlightGate) _inFlight.Remove(runId);
					}
				}
				catch (Exception e)
				{
					_onError(e, null);
				}
			}
		}
	}

	private async Task<int> PurgeAsync(long nowMs, CancellationToken ct)
	{
		if (_retention is null || nowMs < _nextPurgeAtMs) return 0;

		_nextPurgeAtMs = nowMs + _retention.EveryMs;
		return await _store.PurgeSettledAsync(
			nowMs - _retention.AfterMs, _retention.Batch, _retention.IncludeDead, ct);
	}

	public async ValueTask DisposeAsync()
	{
		await StopAsync();
		_stop.Dispose();
	}

	private async Task<int> MaterializeAsync(long nowMs, CancellationToken ct)
	{
		var materialized = 0;

		foreach (var record in await _scheduleStore.ListDueAsync(nowMs, MaxSchedulesPerTick, ct))
		{
			if (record.NextFireAtMs is null) continue;

			// Walk the occurrences that have come due since the last pass.
			var due = new List<long>();
			long? cursor = record.NextFireAtMs;

			while (cursor is { } fire && fire <= nowMs && due.Count < MaxCatchUp)
			{
				due.Add(fire);
				cursor = ScheduleEvaluator.NextFire(record.Spec, fire);
			}

			if (due.Count == 0) continue;

			var toEnqueue = record.Misfire switch
			{
				MisfirePolicy.FireAll => due,
				MisfirePolicy.Skip => [],
				_ => [due[^1]]
			};

			foreach (var fireAt in toEnqueue)
			{
				// Enqueue before advancing. The key makes the insert idempotent,
				// so a crash in between costs a repeated attempt rather than a
				// lost occurrence.
				if (await _store.EnqueueAsync(RequestFor(record, fireAt), ct) is not null) materialized++;
			}

			// A chained schedule re-arms when its run settles, not here.
			await _scheduleStore.AdvanceAsync(
				record.Name, ChainsOnSettle(record.Spec) ? null : cursor, due[^1], nowMs, ct);
		}

		return materialized;
	}

	private JobRunRequest RequestFor(ScheduleRecord record, long fireAtMs) => new()
	{
		Handler = record.Handler,
		Payload = record.Payload,
		// Jitter moves when the run happens but not its identity. Keying on the
		// jittered time would let two workers compute different keys for the same
		// occurrence and enqueue it twice.
		RunAtMs = fireAtMs + JitterFor(record.Spec),
		ScheduleId = record.Name,
		IdempotencyKey = $"{record.Name}:{fireAtMs}",
		MaxAttempts = record.MaxAttempts ?? _retry.MaxAttempts
	};

	// Fixed delay measures from the end of the previous run, so its next
	// occurrence is only knowable once the current one settles. Every other kind
	// sits on a timeline the materializer can walk ahead of time.
	private static bool ChainsOnSettle(ScheduleSpec spec)
		=> spec is IntervalSpec { Mode: IntervalMode.FixedDelay };

	private long JitterFor(ScheduleSpec spec)
		=> spec is IntervalSpec { JitterMs: > 0 } interval
			? (long)Math.Round(_random() * interval.JitterMs)
			: 0;

	// The successor to enqueue in the same call that settles this run. Null for
	// one off work and for schedules the materializer already walks forward.
	private async Task<JobRunRequest?> ChainForAsync(JobRun run, long nowMs, CancellationToken ct)
	{
		if (run.ScheduleId is null) return null;

		var record = await _scheduleStore.GetAsync(run.ScheduleId, ct);
		if (record is null || !record.Enabled || !ChainsOnSettle(record.Spec)) return null;

		var fireAt = ScheduleEvaluator.NextFire(record.Spec, nowMs);
		return fireAt is null ? null : RequestFor(record, fireAt.Value);
	}

	private async Task<Outcome> DispatchAsync(JobRun run, CancellationToken ct)
	{
		var job = _registry.Resolve(run.Handler);

		if (job is null)
		{
			// Retrying cannot help in this process. A durable multi process
			// deployment should instead claim only handlers it knows about.
			var at = _clock.NowMs;
			var error = $"no handler registered for '{run.Handler}'";
			await _store.DeadLetterAsync(run.Id, error, await ChainForAsync(run, at, ct), at, ct);
			_onError(new InvalidOperationException(error), run);
			return Outcome.Dead;
		}

		var context = new JobContext
		{
			RunId = run.Id,
			Attempt = run.Attempt,
			MaxAttempts = run.MaxAttempts,
			CancellationToken = _stop.Token,
			Heartbeat = () => _store.HeartbeatAsync(run.Id, _clock.NowMs + _leaseMs, ct)
		};

		object? payload;
		try
		{
			// Converting is done before the handler runs and outside its try, so a
			// payload that will not convert is never mistaken for a handler that
			// threw. Retrying cannot change what is already stored, so it is
			// permanent.
			payload = job.ConvertPayload(run.Payload);
		}
		catch (Exception e)
		{
			return await SettleFailureAsync(run, new PayloadException(job.Name, e), ct, forcePermanent: true);
		}

		lock (_inFlightGate) _inFlight.Add(run.Id);
		try
		{
			await job.InvokeAsync(payload, context);
		}
		catch (Exception e)
		{
			return await SettleFailureAsync(run, e, ct);
		}
		finally
		{
			lock (_inFlightGate) _inFlight.Remove(run.Id);
		}

		var settledAt = _clock.NowMs;
		await _store.CompleteAsync(run.Id, await ChainForAsync(run, settledAt, ct), settledAt, ct);
		return Outcome.Succeeded;
	}

	private async Task<Outcome> SettleFailureAsync(
		JobRun run, Exception error, CancellationToken ct, bool forcePermanent = false)
	{
		var at = _clock.NowMs;
		_onError(error, run);

		if (!forcePermanent && error is not PermanentJobException && run.Attempt < run.MaxAttempts)
		{
			var delay = _retry.DelayMs(run.Attempt, _random);
			await _store.RetryAsync(run.Id, error.Message, at + delay, at, ct);
			return Outcome.Retried;
		}

		await _store.DeadLetterAsync(run.Id, error.Message, await ChainForAsync(run, at, ct), at, ct);
		return Outcome.Dead;
	}

	private enum Outcome { Succeeded, Retried, Dead }

}
