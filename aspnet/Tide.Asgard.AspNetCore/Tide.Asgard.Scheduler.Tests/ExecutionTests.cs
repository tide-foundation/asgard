// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Globalization;
using Tide.Asgard.Scheduler.Execution;

namespace Tide.Asgard.Scheduler.Tests;

// Mirrors tests/ts/execution.test.js case for case.
internal static class ExecutionTests
{
	private static readonly long T0 = DateTimeOffset
		.Parse("2026-08-17T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
		.ToUnixTimeMilliseconds();

	// Fixed backoff so a run lands on a predictable instant.
	private static readonly RetryPolicy NoJitter = new()
	{
		MaxAttempts = 3, BaseMs = 1_000, CapMs = 60_000, Multiplier = 2, Jitter = JitterMode.None
	};

	private sealed record Harness(
		FakeClock Clock, InMemoryJobStore Store, HandlerRegistry Registry, Worker Worker);

	private static Harness NewHarness(
		Action<Exception, JobRun?>? onError = null,
		long pollIntervalMs = 1_000,
		bool claimOnlyRegisteredHandlers = true,
		RetentionPolicy? retention = null)
	{
		var clock = new FakeClock(T0);
		var store = new InMemoryJobStore();
		var registry = new HandlerRegistry();
		var worker = new Worker(new WorkerOptions
		{
			Store = store,
			Registry = registry,
			Clock = clock,
			Owner = "test",
			LeaseMs = 30_000,
			PollIntervalMs = pollIntervalMs,
			ClaimOnlyRegisteredHandlers = claimOnlyRegisteredHandlers,
			Retention = retention,
			Retry = NoJitter,
			Random = () => 0.5,
			OnError = onError
		});
		return new Harness(clock, store, registry, worker);
	}

	public static void Run(TestRunner runner)
	{
		RetryBackoff(runner);
		Store(runner);
		OneOffJobs(runner);
		Definitions(runner);
		PayloadHandling(runner);
		FailureHandling(runner);
		RecurringSchedules(runner);
		DurableSchedules(runner);
		Admin(runner);
		Retention(runner);
		Stats(runner);
		LeaseRenewal(runner);
		Notifiers(runner);
		Lifecycle(runner);
	}

	private static void Definitions(TestRunner runner)
	{
		runner.Suite("job definitions");

		runner.TestAsync("enqueueing a definition registers it", async () =>
		{
			var h = NewHarness();
			var greet = Job.Define("greet", _ => { });

			Assert.Equal(false, h.Registry.Has("greet"));
			await h.Worker.EnqueueAsync(greet);
			Assert.Equal(true, h.Registry.Has("greet"), "no separate registration step");
		});

		runner.TestAsync("scheduling a definition registers it", async () =>
		{
			var h = NewHarness();
			await h.Worker.AddScheduleAsync(
				ScheduleDefinition.For("s", "on 03:00", Job.Define("sweep", _ => { })));

			Assert.Equal(true, h.Registry.Has("sweep"));
		});

		runner.TestAsync("a job can carry its own attempt limit", async () =>
		{
			var h = NewHarness();
			await h.Worker.EnqueueAsync(Job.Define("careful", _ => { }, maxAttempts: 9));

			Assert.Equal(9, h.Store.All()[0].MaxAttempts);
		});

		runner.TestAsync("an enqueue option beats the job's own limit", async () =>
		{
			var h = NewHarness();
			await h.Worker.EnqueueAsync(Job.Define("careful", _ => { }, maxAttempts: 9), maxAttempts: 2);

			Assert.Equal(2, h.Store.All()[0].MaxAttempts);
		});

		runner.Test("registering the same name twice is refused", () =>
		{
			var h = NewHarness();
			h.Registry.Register(Job.Define("dup", _ => { }));

			try
			{
				h.Registry.Register(Job.Define("dup", _ => { }));
				throw new Exception("expected the second registration to be refused");
			}
			catch (InvalidOperationException e)
			{
				Assert.Contains("already registered", e.Message);
			}
		});

		runner.TestAsync("jobs and schedules can be given to the constructor", async () =>
		{
			var clock = new FakeClock(T0);
			var store = new InMemoryJobStore();
			var fired = new List<long>();
			var sweep = Job.Define("sweep", _ => fired.Add(clock.NowMs));

			var worker = await Worker.CreateAsync(new WorkerOptions
			{
				Store = store,
				Clock = clock,
				Retry = NoJitter,
				Jobs = [sweep],
				Schedules = [ScheduleDefinition.For("half-minute", "on second=*/30", sweep)]
			});

			clock.Advance(30_000);
			Assert.Equal(1, (await worker.TickAsync()).Succeeded);
			Assert.Sequence([T0 + 30_000], fired);
		});

		runner.TestAsync("Worker.CreateAsync applies the schema when the store has one", async () =>
		{
			var store = new SchemaCountingStore();
			var worker = await Worker.CreateAsync(new WorkerOptions
			{
				Store = store, Jobs = [Job.Define("noop", _ => { })]
			});

			Assert.Equal(1, store.SchemaApplied);
			Assert.True(worker is not null, "expected a worker");
		});

		runner.TestAsync("Worker.CreateAsync is fine with a store that has no schema", async () =>
		{
			var worker = await Worker.CreateAsync(new WorkerOptions { Store = new InMemoryJobStore() });
			Assert.True(worker is not null, "expected a worker");
		});
	}

	private sealed record Realm(string Id, int Retries);

	private static void PayloadHandling(TestRunner runner)
	{
		runner.Suite("payload handling");

		runner.TestAsync("a typed payload is deserialized for the handler", async () =>
		{
			var h = NewHarness();
			var seen = new List<Realm>();

			await h.Worker.EnqueueAsync(
				Job.Define<Realm>("typed", (payload, _) => seen.Add(payload)),
				new Realm("tide", 2));

			await h.Worker.TickAsync();
			Assert.Equal(1, seen.Count);
			Assert.Equal("tide", seen[0].Id);
			Assert.Equal(2, seen[0].Retries);
		});

		runner.TestAsync("parse runs on dequeue and its result reaches the handler", async () =>
		{
			var h = NewHarness();
			var seen = new List<string>();

			await h.Worker.EnqueueAsync(
				Job.Define<string>(
					"shouty",
					(payload, _) => seen.Add(payload),
					parse: raw => raw?.ToString()?.Trim('"').ToUpperInvariant() ?? ""),
				"tide");

			await h.Worker.TickAsync();
			Assert.Sequence(["TIDE"], seen);
		});

		runner.TestAsync("a payload that fails parse is dead lettered without burning attempts", async () =>
		{
			var h = NewHarness();

			await h.Worker.EnqueueAsync(
				Job.Define<Realm>(
					"strict",
					(_, _) => { },
					parse: _ => throw new InvalidOperationException("realmId must be a string")),
				new Realm("tide", 1),
				maxAttempts: 5);

			// The parse failure surfaces as a permanent failure, since retrying
			// cannot change a stored payload.
			Assert.Equal(1, (await h.Worker.TickAsync()).Dead);
			Assert.Equal(1, h.Store.ByStatus(JobStatus.Dead)[0].Attempt);
		});

		runner.TestAsync("a job with no payload receives null", async () =>
		{
			var h = NewHarness();
			var seen = 0;

			await h.Worker.EnqueueAsync(Job.Define<object?>("bare", (payload, _) =>
			{
				if (payload is null) seen++;
			}));

			await h.Worker.TickAsync();
			Assert.Equal(1, seen);
		});
	}

	// Wraps the in memory store only to prove the schema hook fires. Composition
	// rather than inheritance because InMemoryJobStore is sealed, which is also
	// what a host implementing ISchemaAwareJobStore around an existing store
	// would have to do.
	private sealed class SchemaCountingStore : IJobStore, ISchemaAwareJobStore
	{
		private readonly InMemoryJobStore _inner = new();

		public int SchemaApplied { get; private set; }

		public Task EnsureSchemaAsync(CancellationToken ct = default)
		{
			SchemaApplied++;
			return Task.CompletedTask;
		}

		public Task<JobRun?> EnqueueAsync(JobRunRequest request, CancellationToken ct = default)
			=> _inner.EnqueueAsync(request, ct);

		public Task<IReadOnlyList<JobRun>> ClaimDueAsync(
			string owner, long nowMs, long leaseMs, int max,
			IReadOnlyCollection<string>? handlers = null, CancellationToken ct = default)
			=> _inner.ClaimDueAsync(owner, nowMs, leaseMs, max, handlers, ct);

		public Task<bool> HeartbeatAsync(string runId, long leaseUntilMs, CancellationToken ct = default)
			=> _inner.HeartbeatAsync(runId, leaseUntilMs, ct);

		public Task CompleteAsync(string runId, JobRunRequest? next, long nowMs, CancellationToken ct = default)
			=> _inner.CompleteAsync(runId, next, nowMs, ct);

		public Task RetryAsync(string runId, string error, long runAtMs, long nowMs, CancellationToken ct = default)
			=> _inner.RetryAsync(runId, error, runAtMs, nowMs, ct);

		public Task DeadLetterAsync(
			string runId, string error, JobRunRequest? next, long nowMs, CancellationToken ct = default)
			=> _inner.DeadLetterAsync(runId, error, next, nowMs, ct);

		public Task<int> ReapExpiredAsync(long nowMs, CancellationToken ct = default)
			=> _inner.ReapExpiredAsync(nowMs, ct);

		public Task<int> PurgeSettledAsync(
			long beforeMs, int limit, bool includeDead = false, CancellationToken ct = default)
			=> _inner.PurgeSettledAsync(beforeMs, limit, includeDead, ct);

		public Task<bool> CancelAsync(string runId, long nowMs, CancellationToken ct = default)
			=> _inner.CancelAsync(runId, nowMs, ct);

		public Task<bool> RequeueAsync(
			string runId, long runAtMs, long nowMs, CancellationToken ct = default)
			=> _inner.RequeueAsync(runId, runAtMs, nowMs, ct);

		public Task<JobStoreStats> StatsAsync(long nowMs, CancellationToken ct = default)
			=> _inner.StatsAsync(nowMs, ct);

		public Task<JobRun?> GetAsync(string runId, CancellationToken ct = default)
			=> _inner.GetAsync(runId, ct);
	}

	private static Harness NewHarness(IScheduleStore scheduleStore)
	{
		var clock = new FakeClock(T0);
		var store = new InMemoryJobStore();
		var registry = new HandlerRegistry();
		var worker = new Worker(new WorkerOptions
		{
			Store = store, Registry = registry, Clock = clock, Owner = "test",
			ScheduleStore = scheduleStore, Retry = NoJitter, Random = () => 0.5
		});
		return new Harness(clock, store, registry, worker);
	}

	private static void DurableSchedules(TestRunner runner)
	{
		runner.Suite("durable schedules");

		runner.TestAsync("re-registering keeps a schedule paused", async () =>
		{
			var scheduleStore = new InMemoryScheduleStore();
			var definition = ScheduleDefinition.For(
				"nightly", "on 03:00", Job.Define("sweep", _ => { }));

			var first = NewHarness(scheduleStore);
			await first.Worker.AddScheduleAsync(definition);
			Assert.Equal(true, await first.Worker.PauseScheduleAsync("nightly"));

			// Standing in for a redeploy: a fresh worker over the same store.
			var second = NewHarness(scheduleStore);
			await second.Worker.AddScheduleAsync(definition);

			var record = await second.Worker.GetScheduleAsync("nightly");
			Assert.Equal(false, record!.Enabled, "a redeploy must not silently resume it");
		});

		runner.TestAsync("re-registering keeps its place in time", async () =>
		{
			var h = NewHarness();
			var definition = ScheduleDefinition.For(
				"nightly", "on 03:00", Job.Define("sweep", _ => { }));

			await h.Worker.AddScheduleAsync(definition);
			var before = (await h.Worker.GetScheduleAsync("nightly"))!.NextFireAtMs;

			h.Clock.Advance(60_000);
			await h.Worker.AddScheduleAsync(definition);

			Assert.Equal(before, (await h.Worker.GetScheduleAsync("nightly"))!.NextFireAtMs);
		});

		runner.TestAsync("changing the expression moves the next fire time", async () =>
		{
			var h = NewHarness();
			var sweep = Job.Define("sweep", _ => { });

			await h.Worker.AddScheduleAsync(ScheduleDefinition.For("nightly", "on 03:00", sweep));
			var before = (await h.Worker.GetScheduleAsync("nightly"))!.NextFireAtMs!.Value;

			await h.Worker.AddScheduleAsync(ScheduleDefinition.For("nightly", "on 04:00", sweep));
			var after = (await h.Worker.GetScheduleAsync("nightly"))!.NextFireAtMs!.Value;

			Assert.Equal(3_600_000L, after - before);
		});

		runner.TestAsync("a paused schedule stops materializing and resumes on demand", async () =>
		{
			var h = NewHarness();
			await h.Worker.AddScheduleAsync(ScheduleDefinition.For(
				"half-minute", "on second=*/30", Job.Define("sweep", _ => { })));

			h.Clock.Advance(30_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Materialized);

			Assert.Equal(true, await h.Worker.PauseScheduleAsync("half-minute"));
			h.Clock.Advance(30_000);
			Assert.Equal(0, (await h.Worker.TickAsync()).Materialized, "paused means paused");

			Assert.Equal(true, await h.Worker.ResumeScheduleAsync("half-minute"));
			h.Clock.Advance(30_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Materialized);
		});

		runner.TestAsync("pausing something that does not exist says so", async () =>
			Assert.Equal(false, await NewHarness().Worker.PauseScheduleAsync("nope")));

		runner.TestAsync("removing a schedule stops it firing", async () =>
		{
			var h = NewHarness();
			await h.Worker.AddScheduleAsync(ScheduleDefinition.For(
				"half-minute", "on second=*/30", Job.Define("sweep", _ => { })));

			Assert.Equal(true, await h.Worker.RemoveScheduleAsync("half-minute"));
			h.Clock.Advance(60_000);
			Assert.Equal(0, (await h.Worker.TickAsync()).Materialized);
			Assert.Equal(0, (await h.Worker.ListSchedulesAsync()).Count);
		});

		runner.TestAsync("listing reports what is registered", async () =>
		{
			var h = NewHarness();
			await h.Worker.AddScheduleAsync(
				ScheduleDefinition.For("b", "on 04:00", Job.Define("j2", _ => { })));
			await h.Worker.AddScheduleAsync(
				ScheduleDefinition.For("a", "on 03:00", Job.Define("j1", _ => { })));

			var listed = await h.Worker.ListSchedulesAsync();
			Assert.Sequence(["a", "b"], listed.Select(x => x.Name));
			Assert.Sequence(["on 03:00", "on 04:00"], listed.Select(x => x.Expr));
			Assert.Equal(true, listed.All(x => x.Enabled));
		});

		runner.TestAsync("a schedule advances its own next fire time", async () =>
		{
			var h = NewHarness();
			await h.Worker.AddScheduleAsync(ScheduleDefinition.For(
				"half-minute", "on second=*/30", Job.Define("sweep", _ => { })));

			h.Clock.Advance(30_000);
			await h.Worker.TickAsync();

			var record = await h.Worker.GetScheduleAsync("half-minute");
			Assert.Equal(T0 + 30_000, record!.LastFireAtMs);
			Assert.Equal(T0 + 60_000, record.NextFireAtMs);
		});
	}

	private static void Admin(TestRunner runner)
	{
		runner.Suite("admin: triggering, cancelling and requeueing");

		runner.TestAsync("trigger runs a schedule now without disturbing its timetable", async () =>
		{
			var h = NewHarness();
			var fired = new List<long>();
			await h.Worker.AddScheduleAsync(ScheduleDefinition.For(
				"nightly", "on 03:00", Job.Define("sweep", _ => fired.Add(h.Clock.NowMs))));

			var before = (await h.Worker.GetScheduleAsync("nightly"))!.NextFireAtMs;

			Assert.True(await h.Worker.TriggerScheduleAsync("nightly") is not null, "expected a run");
			Assert.Equal(1, (await h.Worker.TickAsync()).Succeeded);

			Assert.Sequence([T0], fired);
			Assert.Equal(before, (await h.Worker.GetScheduleAsync("nightly"))!.NextFireAtMs,
				"a manual run must not move the schedule");
		});

		runner.TestAsync("a paused schedule can still be triggered on purpose", async () =>
		{
			var h = NewHarness();
			await h.Worker.AddScheduleAsync(ScheduleDefinition.For(
				"nightly", "on 03:00", Job.Define("sweep", _ => { })));
			await h.Worker.PauseScheduleAsync("nightly");

			Assert.True(await h.Worker.TriggerScheduleAsync("nightly") is not null, "expected a run");
			Assert.Equal(1, (await h.Worker.TickAsync()).Succeeded);
		});

		runner.TestAsync("triggering something that does not exist says so", async () =>
			Assert.True(await NewHarness().Worker.TriggerScheduleAsync("nope") is null, "expected null"));

		runner.TestAsync("cancel stops a pending run", async () =>
		{
			var h = NewHarness();
			var runs = 0;
			var run = await h.Worker.EnqueueAsync(
				Job.Define("later", _ => runs++), runAtMs: T0 + 60_000);

			Assert.Equal(true, await h.Worker.CancelRunAsync(run!.Id));
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Cancelled));

			await h.Worker.TickAsync();
			Assert.Equal(0, runs);
		});

		runner.TestAsync("cancelling a settled run says so rather than pretending", async () =>
		{
			var h = NewHarness();
			var run = await h.Worker.EnqueueAsync(Job.Define("quick", _ => { }));
			await h.Worker.TickAsync();

			Assert.Equal(false, await h.Worker.CancelRunAsync(run!.Id));
		});

		runner.TestAsync("requeue puts a dead run back with a fresh set of attempts", async () =>
		{
			var h = NewHarness();
			var attempts = 0;
			var flaky = Job.Define("flaky", _ =>
			{
				attempts++;
				if (attempts == 1) throw new PermanentJobException("nope");
			});

			var run = await h.Worker.EnqueueAsync(flaky, maxAttempts: 1);
			Assert.Equal(1, (await h.Worker.TickAsync()).Dead);

			h.Clock.Advance(1_000);
			Assert.Equal(true, await h.Worker.RequeueRunAsync(run!.Id));

			var requeued = await h.Store.GetAsync(run.Id);
			Assert.Equal(JobStatus.Pending, requeued!.Status);
			Assert.Equal(0, requeued.Attempt, "attempts start over");
			Assert.True(requeued.LastError is null, "the error should be cleared");

			Assert.Equal(1, (await h.Worker.TickAsync()).Succeeded);
		});

		runner.TestAsync("requeue can also revive a cancelled run", async () =>
		{
			var h = NewHarness();
			var run = await h.Worker.EnqueueAsync(
				Job.Define("later", _ => { }), runAtMs: T0 + 60_000);

			await h.Worker.CancelRunAsync(run!.Id);
			Assert.Equal(true, await h.Worker.RequeueRunAsync(run.Id));
			Assert.Equal(JobStatus.Pending, (await h.Store.GetAsync(run.Id))!.Status);
		});

		runner.TestAsync("requeueing a run that is not settled says so", async () =>
		{
			var h = NewHarness();
			var run = await h.Worker.EnqueueAsync(
				Job.Define("later", _ => { }), runAtMs: T0 + 60_000);

			Assert.Equal(false, await h.Worker.RequeueRunAsync(run!.Id));
		});

		runner.TestAsync("cancelled runs are counted separately", async () =>
		{
			var h = NewHarness();
			var run = await h.Worker.EnqueueAsync(
				Job.Define("later", _ => { }), runAtMs: T0 + 60_000);
			await h.Worker.CancelRunAsync(run!.Id);

			var stats = await h.Worker.StatsAsync();
			Assert.Equal(1, stats.Cancelled);
			Assert.Equal(0, stats.Pending);
		});
	}

	private static void Retention(TestRunner runner)
	{
		runner.Suite("retention");

		runner.TestAsync("purges settled runs past the cutoff and leaves the rest", async () =>
		{
			var store = new InMemoryJobStore();

			var old = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			var recent = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			var pending = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });

			await store.CompleteAsync(old!.Id, null, T0);
			await store.CompleteAsync(recent!.Id, null, T0 + 10_000);

			Assert.Equal(1, await store.PurgeSettledAsync(T0 + 5_000, 100));
			Assert.True(await store.GetAsync(old.Id) is null, "the old run should be gone");
			Assert.True(await store.GetAsync(recent.Id) is not null, "the recent run should remain");
			Assert.True(await store.GetAsync(pending!.Id) is not null, "the pending run should remain");
		});

		runner.TestAsync("dead runs are kept unless asked for", async () =>
		{
			var store = new InMemoryJobStore();
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.DeadLetterAsync(run!.Id, "gave up", null, T0);

			Assert.Equal(0, await store.PurgeSettledAsync(T0 + 5_000, 100));
			Assert.Equal(1, await store.PurgeSettledAsync(T0 + 5_000, 100, includeDead: true));
		});

		runner.TestAsync("the batch limit bounds a single sweep", async () =>
		{
			var store = new InMemoryJobStore();
			for (var i = 0; i < 5; i++)
			{
				var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
				await store.CompleteAsync(run!.Id, null, T0);
			}

			Assert.Equal(2, await store.PurgeSettledAsync(T0 + 1, 2));
			Assert.Equal(2, await store.PurgeSettledAsync(T0 + 1, 2));
			Assert.Equal(1, await store.PurgeSettledAsync(T0 + 1, 2));
			Assert.Equal(0, await store.PurgeSettledAsync(T0 + 1, 2));
		});

		runner.TestAsync("the worker sweeps on its own interval", async () =>
		{
			var h = NewHarness(retention: new RetentionPolicy { AfterMs = 60_000, EveryMs = 30_000 });
			var done = Job.Define("done", _ => { });

			await h.Worker.EnqueueAsync(done);
			await h.Worker.TickAsync();
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Succeeded));

			// Not old enough yet, and the sweep interval has not come round either.
			h.Clock.Advance(30_000);
			Assert.Equal(0, (await h.Worker.TickAsync()).Purged);

			h.Clock.Advance(61_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Purged);
			Assert.Equal(0, h.Store.All().Count);
		});
	}

	private static void Stats(TestRunner runner)
	{
		runner.Suite("stats");

		runner.TestAsync("counts by status and reports the oldest waiting run", async () =>
		{
			var store = new InMemoryJobStore();

			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 + 5_000 });
			var done = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.CompleteAsync(done!.Id, null, T0);

			var stats = await store.StatsAsync(T0 + 10_000);
			Assert.Equal(2, stats.Pending);
			Assert.Equal(1, stats.Succeeded);
			Assert.Equal(0, stats.Dead);
			Assert.Equal(10_000L, stats.OldestPendingAgeMs);
		});

		runner.TestAsync("a run that is not due yet does not count as waiting", async () =>
		{
			var store = new InMemoryJobStore();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 + 60_000 });

			var stats = await store.StatsAsync(T0);
			Assert.Equal(1, stats.Pending);
			Assert.Equal(0L, stats.OldestPendingAgeMs);
		});
	}

	// These run on the real clock because they are about elapsed time relative to
	// a lease. Timings are kept small and the margins are wide.
	private static void LeaseRenewal(TestRunner runner)
	{
		runner.Suite("automatic lease renewal");

		runner.TestAsync("Start keeps the lease alive for as long as the handler runs", async () =>
		{
			var store = new InMemoryJobStore();
			var finished = false;
			var stolen = 0;

			var busy = Job.Define<object?>("slow", async (_, _) =>
			{
				await Task.Delay(600);
				finished = true;
			});
			var thief = Job.Define("slow", _ => Interlocked.Increment(ref stolen));

			await using var owner = new Worker(new WorkerOptions
			{
				Store = store, Jobs = [busy], Owner = "owner",
				LeaseMs = 200, HeartbeatMs = 50, PollIntervalMs = 20
			});
			await using var other = new Worker(new WorkerOptions
			{
				Store = store, Jobs = [thief], Owner = "other", LeaseMs = 200
			});

			await owner.EnqueueAsync(busy);
			owner.Start();

			// Well past the lease, so without renewal this would already be stealable.
			await Task.Delay(350);
			await other.TickAsync();

			await Task.Delay(500);
			await owner.StopAsync();

			Assert.Equal(true, finished);
			Assert.Equal(0, stolen, "the lease was renewed, so nobody could take it");
		});

		runner.TestAsync("a bare tick does not renew, and the lease lapses", async () =>
		{
			var store = new InMemoryJobStore();
			var stolen = 0;

			var busy = Job.Define<object?>("slow", async (_, _) => await Task.Delay(600));
			var thief = Job.Define("slow", _ => Interlocked.Increment(ref stolen));

			await using var owner = new Worker(new WorkerOptions
			{
				Store = store, Jobs = [busy], Owner = "owner", LeaseMs = 200
			});
			await using var other = new Worker(new WorkerOptions
			{
				Store = store, Jobs = [thief], Owner = "other", LeaseMs = 200
			});

			await owner.EnqueueAsync(busy);

			// Deliberately not awaited: the tick is blocked on the handler, which
			// is exactly why renewal cannot live inside it.
			var ticking = owner.TickAsync();

			await Task.Delay(350);
			var result = await other.TickAsync();

			Assert.Equal(1, result.Reaped, "the lease expired while the handler was still working");
			Assert.Equal(1, stolen, "and another worker picked the run up");

			await ticking;
		});
	}

	private static void RetryBackoff(TestRunner runner)
	{
		runner.Suite("retry backoff");

		runner.Test("doubles from the base delay", () =>
		{
			Assert.Equal(1_000L, NoJitter.DelayMs(1));
			Assert.Equal(2_000L, NoJitter.DelayMs(2));
			Assert.Equal(4_000L, NoJitter.DelayMs(3));
		});

		runner.Test("caps before jitter is applied", () =>
			Assert.Equal(3_000L, (NoJitter with { CapMs = 3_000 }).DelayMs(9)));

		runner.Test("full jitter spans zero to the delay", () =>
		{
			var policy = NoJitter with { Jitter = JitterMode.Full };
			Assert.Equal(0L, policy.DelayMs(1, () => 0));
			Assert.Equal(1_000L, policy.DelayMs(1, () => 1));
			Assert.Equal(500L, policy.DelayMs(1, () => 0.5));
		});

		runner.Test("equal jitter keeps half the delay", () =>
		{
			var policy = NoJitter with { Jitter = JitterMode.Equal };
			Assert.Equal(500L, policy.DelayMs(1, () => 0));
			Assert.Equal(1_000L, policy.DelayMs(1, () => 1));
		});

		runner.Test("ShouldRetry counts attempts already started", () =>
		{
			Assert.Equal(true, NoJitter.ShouldRetry(2));
			Assert.Equal(false, NoJitter.ShouldRetry(3));
		});
	}

	private static void Store(TestRunner runner)
	{
		runner.Suite("in memory store");

		runner.TestAsync("enqueue then claim leases the run and counts the attempt", async () =>
		{
			var store = new InMemoryJobStore();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0, MaxAttempts = 3 });

			var claimed = await store.ClaimDueAsync("worker-a", T0, 30_000, 10);
			Assert.Equal(1, claimed.Count);
			Assert.Equal(JobStatus.Leased, claimed[0].Status);
			Assert.Equal(1, claimed[0].Attempt);
			Assert.Equal("worker-a", claimed[0].LeaseOwner);
			Assert.Equal(T0 + 30_000, claimed[0].LeaseExpiresAtMs);
		});

		runner.TestAsync("a leased run is not handed to a second claimer", async () =>
		{
			var store = new InMemoryJobStore();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });

			Assert.Equal(1, (await store.ClaimDueAsync("worker-a", T0, 30_000, 10)).Count);
			Assert.Equal(0, (await store.ClaimDueAsync("worker-b", T0, 30_000, 10)).Count);
		});

		runner.TestAsync("runs in the future are not claimed", async () =>
		{
			var store = new InMemoryJobStore();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 + 5_000 });

			Assert.Equal(0, (await store.ClaimDueAsync("worker-a", T0, 30_000, 10)).Count);
			Assert.Equal(1, (await store.ClaimDueAsync("worker-a", T0 + 5_000, 30_000, 10)).Count);
		});

		runner.TestAsync("a repeated idempotency key is discarded", async () =>
		{
			var store = new InMemoryJobStore();
			var request = new JobRunRequest { Handler = "h", RunAtMs = T0, IdempotencyKey = "nightly:1" };

			Assert.True(await store.EnqueueAsync(request) is not null, "first insert should win");
			Assert.True(await store.EnqueueAsync(request) is null, "second insert should be discarded");
			Assert.Equal(1, store.All().Count);
		});

		runner.TestAsync("an expired lease returns the run to pending", async () =>
		{
			var store = new InMemoryJobStore();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.ClaimDueAsync("worker-a", T0, 1_000, 10);

			Assert.Equal(0, await store.ReapExpiredAsync(T0 + 500));
			Assert.Equal(1, await store.ReapExpiredAsync(T0 + 1_001));
			Assert.Equal(1, store.CountByStatus(JobStatus.Pending));
		});

		runner.TestAsync("heartbeat pushes the lease out and fails once the run is gone", async () =>
		{
			var store = new InMemoryJobStore();
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.ClaimDueAsync("worker-a", T0, 1_000, 10);

			Assert.Equal(true, await store.HeartbeatAsync(run!.Id, T0 + 60_000));
			Assert.Equal(0, await store.ReapExpiredAsync(T0 + 1_001));

			await store.CompleteAsync(run.Id, null, T0 + 2_000);
			Assert.Equal(false, await store.HeartbeatAsync(run.Id, T0 + 90_000));
		});

		runner.TestAsync("complete chains the successor in the same call", async () =>
		{
			var store = new InMemoryJobStore();
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.CompleteAsync(
				run!.Id, new JobRunRequest { Handler = "h", RunAtMs = T0 + 60_000 }, T0);

			Assert.Equal(1, store.CountByStatus(JobStatus.Succeeded));
			Assert.Equal(1, store.CountByStatus(JobStatus.Pending));
		});
	}

	private static void OneOffJobs(TestRunner runner)
	{
		runner.Suite("worker: one off jobs");

		runner.TestAsync("runs a job and passes the payload through", async () =>
		{
			var h = NewHarness();
			var seen = new List<(string? Payload, int Attempt)>();
			var greet = Job.Define<string>("greet", (payload, ctx) => seen.Add((payload, ctx.Attempt)));

			await h.Worker.EnqueueAsync(greet, "asgard");
			var result = await h.Worker.TickAsync();

			Assert.Equal(1, result.Claimed);
			Assert.Equal(1, result.Succeeded);
			Assert.Equal(1, seen.Count);
			Assert.Equal("asgard", seen[0].Payload);
			Assert.Equal(1, seen[0].Attempt);
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Succeeded));
		});

		runner.TestAsync("a job scheduled for later is not run yet", async () =>
		{
			var h = NewHarness();
			var runs = 0;
			var later = Job.Define("later", _ => runs++);

			await h.Worker.EnqueueAsync(later, runAtMs: T0 + 10_000);
			Assert.Equal(0, (await h.Worker.TickAsync()).Claimed);

			h.Clock.Advance(10_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Succeeded);
			Assert.Equal(1, runs);
		});

		runner.TestAsync("a run for an unregistered handler is left alone, not claimed", async () =>
		{
			var h = NewHarness();
			await h.Worker.EnqueueByNameAsync("missing");

			Assert.Equal(0, (await h.Worker.TickAsync()).Claimed, "another worker may be able to run it");
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Pending));
		});

		runner.TestAsync("only registered handlers are claimed when others are due", async () =>
		{
			var h = NewHarness();
			var known = Job.Define("known", _ => { });

			await h.Worker.EnqueueByNameAsync("missing");
			await h.Worker.EnqueueAsync(known);

			var result = await h.Worker.TickAsync();
			Assert.Equal(1, result.Claimed);
			Assert.Equal(1, result.Succeeded);
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Pending), "the unknown one still waits");
		});

		runner.TestAsync("with filtering off an unknown handler goes to dead", async () =>
		{
			var h = NewHarness(claimOnlyRegisteredHandlers: false);
			await h.Worker.EnqueueByNameAsync("missing");

			Assert.Equal(1, (await h.Worker.TickAsync()).Dead);
			Assert.Contains("no handler registered", h.Store.ByStatus(JobStatus.Dead)[0].LastError);
		});
	}

	private static void FailureHandling(TestRunner runner)
	{
		runner.Suite("worker: failure handling");

		runner.TestAsync("retries with backoff then succeeds", async () =>
		{
			var h = NewHarness();
			var attempts = 0;
			var flaky = Job.Define("flaky", _ =>
			{
				attempts++;
				if (attempts < 3) throw new Exception($"boom {attempts}");
			});

			await h.Worker.EnqueueAsync(flaky);

			Assert.Equal(1, (await h.Worker.TickAsync()).Retried);
			Assert.Equal(T0 + 1_000, h.Store.All()[0].RunAtMs, "first retry waits one base delay");

			h.Clock.Advance(1_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Retried);
			Assert.Equal(T0 + 3_000, h.Store.All()[0].RunAtMs, "second retry doubles");

			h.Clock.Advance(2_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Succeeded);
			Assert.Equal(3, attempts);
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Succeeded));
		});

		runner.TestAsync("dead letters once attempts run out", async () =>
		{
			var h = NewHarness();
			var doomed = Job.Define("doomed", _ => throw new Exception("always fails"));

			await h.Worker.EnqueueAsync(doomed, maxAttempts: 2);

			Assert.Equal(1, (await h.Worker.TickAsync()).Retried);
			h.Clock.Advance(1_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Dead);

			var run = h.Store.ByStatus(JobStatus.Dead)[0];
			Assert.Equal(2, run.Attempt);
			Assert.Equal("always fails", run.LastError);
		});

		runner.TestAsync("a permanent error skips the remaining attempts", async () =>
		{
			var h = NewHarness();
			var bad = Job.Define("bad-payload", _ => throw new PermanentJobException("payload is malformed"));

			await h.Worker.EnqueueAsync(bad, maxAttempts: 5);
			Assert.Equal(1, (await h.Worker.TickAsync()).Dead);

			Assert.Equal(1, h.Store.ByStatus(JobStatus.Dead)[0].Attempt,
				"should not have burned the other attempts");
		});

		runner.TestAsync("failures are reported to OnError", async () =>
		{
			var seen = new List<string>();
			var h = NewHarness(onError: (e, run) => seen.Add($"{e.Message}:{run?.Id}"));
			var noisy = Job.Define("noisy", _ => throw new Exception("kaboom"));

			await h.Worker.EnqueueAsync(noisy);
			await h.Worker.TickAsync();

			Assert.Sequence(["kaboom:run-1"], seen);
		});
	}

	private static void RecurringSchedules(TestRunner runner)
	{
		runner.Suite("worker: recurring schedules");

		runner.TestAsync("a calendar schedule materializes each occurrence once", async () =>
		{
			var h = NewHarness();
			var fired = new List<long>();
			var sweep = Job.Define("sweep", _ => fired.Add(h.Clock.NowMs));

			await h.Worker.AddScheduleAsync(ScheduleDefinition.For("sweep-every-30s", "on second=*/30", sweep));

			Assert.Equal(0, (await h.Worker.TickAsync()).Materialized, "nothing is due yet");

			h.Clock.Advance(30_000);
			var result = await h.Worker.TickAsync();
			Assert.Equal(1, result.Materialized);
			Assert.Equal(1, result.Succeeded);

			h.Clock.Advance(30_000);
			result = await h.Worker.TickAsync();
			Assert.Equal(1, result.Materialized);
			Assert.Equal(1, result.Succeeded);

			Assert.Sequence([T0 + 30_000, T0 + 60_000], fired);
		});

		runner.TestAsync("a fixed delay schedule chains when the run settles", async () =>
		{
			var h = NewHarness();
			var runs = 0;
			var sync = Job.Define("sync", _ => runs++);

			await h.Worker.AddScheduleAsync(ScheduleDefinition.For("sync-loop", "every 10s", sync));

			h.Clock.Advance(10_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Succeeded);
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Pending), "successor was chained on settle");

			h.Clock.Advance(10_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Succeeded);
			Assert.Equal(2, runs);
		});

		runner.TestAsync("a schedule survives a run that dead letters", async () =>
		{
			var h = NewHarness();
			var brittle = Job.Define("brittle", _ => throw new PermanentJobException("nope"));

			await h.Worker.AddScheduleAsync(
				ScheduleDefinition.For("brittle-loop", "every 10s", brittle, maxAttempts: 1));

			h.Clock.Advance(10_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Dead);
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Pending), "chain must outlive a dead run");
		});

		runner.TestAsync("missed occurrences follow the misfire policy", async () =>
		{
			async Task<int> Missed(MisfirePolicy misfire)
			{
				var h = NewHarness();
				var catchUp = Job.Define("catch-up", _ => { });
				await h.Worker.AddScheduleAsync(ScheduleDefinition.For(
					"every-10s", "on second=*/10", catchUp, misfire: misfire));

				h.Clock.Advance(35_000);
				return (await h.Worker.TickAsync()).Materialized;
			}

			Assert.Equal(3, await Missed(MisfirePolicy.FireAll), "10s, 20s and 30s");
			Assert.Equal(1, await Missed(MisfirePolicy.FireOnce), "only the most recent");
			Assert.Equal(0, await Missed(MisfirePolicy.Skip), "abandon what was missed");
		});

		runner.TestAsync("two workers sharing a store materialize an occurrence only once", async () =>
		{
			var clock = new FakeClock(T0);
			var store = new InMemoryJobStore();
			var shared = Job.Define("shared", _ => { });

			WorkerOptions Options(string owner) => new()
			{
				Store = store, Jobs = [shared], Clock = clock,
				Owner = owner, Retry = NoJitter, Random = () => 0.5
			};

			var a = new Worker(Options("a"));
			var b = new Worker(Options("b"));

			var definition = ScheduleDefinition.For("shared-sweep", "on second=*/30", shared);
			await a.AddScheduleAsync(definition);
			await b.AddScheduleAsync(definition);

			clock.Advance(30_000);
			var first = await a.TickAsync();
			var second = await b.TickAsync();

			Assert.Equal(1, first.Materialized);
			Assert.Equal(0, second.Materialized, "the idempotency key discarded the duplicate");
			Assert.Equal(1, store.All().Count);
		});
	}

	private static void Notifiers(TestRunner runner)
	{
		runner.Suite("notifiers");

		runner.TestAsync("wait resolves as soon as it is notified", async () =>
		{
			var notifier = new InMemoryNotifier();
			var started = Environment.TickCount64;

			var waiting = notifier.WaitAsync(5_000);
			await Task.Delay(20);
			await notifier.NotifyAsync();
			await waiting;

			Assert.True(Environment.TickCount64 - started < 1_000,
				"should not have waited out the timeout");
		});

		runner.TestAsync("wait gives up after the timeout when nothing happens", async () =>
		{
			var started = Environment.TickCount64;
			await new InMemoryNotifier().WaitAsync(50);

			Assert.True(Environment.TickCount64 - started >= 45, "should have waited");
		});

		runner.TestAsync("wait returns when the token is cancelled", async () =>
		{
			using var cts = new CancellationTokenSource();
			var waiting = new InMemoryNotifier().WaitAsync(5_000, cts.Token);

			await Task.Delay(20);
			await cts.CancelAsync();
			await waiting;
		});

		runner.TestAsync("an already cancelled token does not wait at all", async () =>
		{
			using var cts = new CancellationTokenSource();
			await cts.CancelAsync();

			var started = Environment.TickCount64;
			await new InMemoryNotifier().WaitAsync(5_000, cts.Token);

			Assert.True(Environment.TickCount64 - started < 1_000, "should have returned at once");
		});

		runner.TestAsync("notifying with nobody waiting is harmless", async () =>
			await new InMemoryNotifier().NotifyAsync());

		runner.TestAsync("every waiter is woken", async () =>
		{
			var notifier = new InMemoryNotifier();
			var waiters = new[]
			{
				notifier.WaitAsync(5_000), notifier.WaitAsync(5_000), notifier.WaitAsync(5_000)
			};

			await Task.Delay(20);
			await notifier.NotifyAsync();
			await Task.WhenAll(waiters);
		});

		// Real clock: the point of a notifier is elapsed time, so there is
		// nothing to assert on a fake one. Kept short, with a wide margin.
		runner.TestAsync("a running worker picks up work without waiting out the poll interval", async () =>
		{
			var store = new InMemoryJobStore();
			var notifier = new InMemoryNotifier();
			long ranAt = 0;

			var quick = Job.Define("quick", _ => Interlocked.Exchange(ref ranAt, Environment.TickCount64));

			WorkerOptions Options(string owner) => new()
			{
				Store = store, Notifier = notifier, Jobs = [quick],
				Owner = owner, PollIntervalMs = 5_000, Retry = NoJitter
			};

			await using var consumer = new Worker(Options("consumer"));
			await using var producer = new Worker(Options("producer"));

			consumer.Start();
			await Task.Delay(100);

			var enqueued = Environment.TickCount64;
			await producer.EnqueueAsync(quick);
			await Task.Delay(400);
			await consumer.StopAsync();

			Assert.True(ranAt != 0, "the job should have run");
			Assert.True(ranAt - enqueued < 1_000, $"woken in {ranAt - enqueued}ms, not after 5s");
		});

		runner.TestAsync("without a notifier the same worker is still waiting", async () =>
		{
			var store = new InMemoryJobStore();
			long ranAt = 0;

			var quick = Job.Define("quick", _ => Interlocked.Exchange(ref ranAt, Environment.TickCount64));

			WorkerOptions Options(string owner) => new()
			{
				Store = store, Jobs = [quick], Owner = owner,
				PollIntervalMs = 5_000, Retry = NoJitter
			};

			await using var consumer = new Worker(Options("consumer"));
			await using var producer = new Worker(Options("producer"));

			consumer.Start();
			await Task.Delay(100);

			await producer.EnqueueAsync(quick);
			await Task.Delay(400);
			await consumer.StopAsync();

			Assert.Equal(0L, ranAt, "polling alone should not have got to it yet");
		});

		runner.TestAsync("a notifier that throws costs latency, not correctness", async () =>
		{
			var store = new InMemoryJobStore();
			var errors = new List<string>();
			var runs = 0;

			await using var worker = new Worker(new WorkerOptions
			{
				Store = store,
				Notifier = new BrokenNotifier(),
				PollIntervalMs = 50,
				Retry = NoJitter,
				OnError = (e, _) => { lock (errors) errors.Add(e.Message); }
			});

			await worker.EnqueueAsync(Job.Define("quick", _ => Interlocked.Increment(ref runs)));

			worker.Start();
			await Task.Delay(400);
			await worker.StopAsync();

			Assert.Equal(1, runs, "the job still ran");
			Assert.True(errors.Count > 0, "and the failures were reported");
		});
	}

	private sealed class BrokenNotifier : IJobNotifier
	{
		public Task NotifyAsync(CancellationToken ct = default)
			=> throw new InvalidOperationException("notify is down");

		public Task WaitAsync(long timeoutMs, CancellationToken ct = default)
			=> throw new InvalidOperationException("wait is down");
	}

	private static void Lifecycle(TestRunner runner)
	{
		runner.Suite("worker: lifecycle");

		runner.TestAsync("start and stop drive the loop without leaking it", async () =>
		{
			var h = NewHarness();
			var ran = new TaskCompletionSource();
			var ticker = Job.Define("tick", _ => ran.TrySetResult());

			await h.Worker.EnqueueAsync(ticker);
			h.Worker.Start();

			// Wait for the loop to actually reach the handler rather than assuming
			// it got there before the stop, which is a scheduling race.
			var finished = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
			await h.Worker.StopAsync();

			Assert.True(finished == ran.Task, "the loop should have run the queued job");
		});
	}
}
