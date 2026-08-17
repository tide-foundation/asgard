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
		FailureHandling(runner);
		RecurringSchedules(runner);
		Retention(runner);
		Stats(runner);
		LeaseRenewal(runner);
		Lifecycle(runner);
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
			h.Registry.Register("done", (_, _) => { });

			await h.Worker.EnqueueAsync("done");
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

			var busy = new HandlerRegistry();
			busy.Register("slow", async (_, _) =>
			{
				await Task.Delay(600);
				finished = true;
			});

			var thief = new HandlerRegistry();
			thief.Register("slow", (_, _) => Interlocked.Increment(ref stolen));

			await using var owner = new Worker(new WorkerOptions
			{
				Store = store, Registry = busy, Owner = "owner",
				LeaseMs = 200, HeartbeatMs = 50, PollIntervalMs = 20
			});
			await using var other = new Worker(new WorkerOptions
			{
				Store = store, Registry = thief, Owner = "other", LeaseMs = 200
			});

			await owner.EnqueueAsync("slow");
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

			var busy = new HandlerRegistry();
			busy.Register("slow", async (_, _) => await Task.Delay(600));

			var thief = new HandlerRegistry();
			thief.Register("slow", (_, _) => Interlocked.Increment(ref stolen));

			await using var owner = new Worker(new WorkerOptions
			{
				Store = store, Registry = busy, Owner = "owner", LeaseMs = 200
			});
			await using var other = new Worker(new WorkerOptions
			{
				Store = store, Registry = thief, Owner = "other", LeaseMs = 200
			});

			await owner.EnqueueAsync("slow");

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
			var seen = new List<(object? Payload, int Attempt)>();
			h.Registry.Register("greet", (payload, ctx) => seen.Add((payload, ctx.Attempt)));

			await h.Worker.EnqueueAsync("greet", "asgard");
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
			h.Registry.Register("later", (_, _) => runs++);

			await h.Worker.EnqueueAsync("later", runAtMs: T0 + 10_000);
			Assert.Equal(0, (await h.Worker.TickAsync()).Claimed);

			h.Clock.Advance(10_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Succeeded);
			Assert.Equal(1, runs);
		});

		runner.TestAsync("a run for an unregistered handler is left alone, not claimed", async () =>
		{
			var h = NewHarness();
			await h.Worker.EnqueueAsync("missing");

			Assert.Equal(0, (await h.Worker.TickAsync()).Claimed, "another worker may be able to run it");
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Pending));
		});

		runner.TestAsync("only registered handlers are claimed when others are due", async () =>
		{
			var h = NewHarness();
			h.Registry.Register("known", (_, _) => { });

			await h.Worker.EnqueueAsync("missing");
			await h.Worker.EnqueueAsync("known");

			var result = await h.Worker.TickAsync();
			Assert.Equal(1, result.Claimed);
			Assert.Equal(1, result.Succeeded);
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Pending), "the unknown one still waits");
		});

		runner.TestAsync("with filtering off an unknown handler goes to dead", async () =>
		{
			var h = NewHarness(claimOnlyRegisteredHandlers: false);
			await h.Worker.EnqueueAsync("missing");

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
			h.Registry.Register("flaky", (_, _) =>
			{
				attempts++;
				if (attempts < 3) throw new Exception($"boom {attempts}");
			});

			await h.Worker.EnqueueAsync("flaky");

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
			h.Registry.Register("doomed", (_, _) => throw new Exception("always fails"));

			await h.Worker.EnqueueAsync("doomed", maxAttempts: 2);

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
			h.Registry.Register("bad-payload", (_, _) => throw new PermanentJobException("payload is malformed"));

			await h.Worker.EnqueueAsync("bad-payload", maxAttempts: 5);
			Assert.Equal(1, (await h.Worker.TickAsync()).Dead);

			Assert.Equal(1, h.Store.ByStatus(JobStatus.Dead)[0].Attempt,
				"should not have burned the other attempts");
		});

		runner.TestAsync("failures are reported to OnError", async () =>
		{
			var seen = new List<string>();
			var h = NewHarness(onError: (e, run) => seen.Add($"{e.Message}:{run?.Id}"));
			h.Registry.Register("noisy", (_, _) => throw new Exception("kaboom"));

			await h.Worker.EnqueueAsync("noisy");
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
			h.Registry.Register("sweep", (_, _) => fired.Add(h.Clock.NowMs));

			h.Worker.AddSchedule(new ScheduleDefinition
			{
				Name = "sweep-every-30s", Expr = "on second=*/30", Handler = "sweep"
			});

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
			h.Registry.Register("sync", (_, _) => runs++);

			h.Worker.AddSchedule(new ScheduleDefinition
			{
				Name = "sync-loop", Expr = "every 10s", Handler = "sync"
			});

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
			h.Registry.Register("brittle", (_, _) => throw new PermanentJobException("nope"));

			h.Worker.AddSchedule(new ScheduleDefinition
			{
				Name = "brittle-loop", Expr = "every 10s", Handler = "brittle", MaxAttempts = 1
			});

			h.Clock.Advance(10_000);
			Assert.Equal(1, (await h.Worker.TickAsync()).Dead);
			Assert.Equal(1, h.Store.CountByStatus(JobStatus.Pending), "chain must outlive a dead run");
		});

		runner.TestAsync("missed occurrences follow the misfire policy", async () =>
		{
			async Task<int> Missed(MisfirePolicy misfire)
			{
				var h = NewHarness();
				h.Registry.Register("catch-up", (_, _) => { });
				h.Worker.AddSchedule(new ScheduleDefinition
				{
					Name = "every-10s", Expr = "on second=*/10", Handler = "catch-up", Misfire = misfire
				});

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
			var registry = new HandlerRegistry();
			registry.Register("shared", (_, _) => { });

			WorkerOptions Options(string owner) => new()
			{
				Store = store, Registry = registry, Clock = clock,
				Owner = owner, Retry = NoJitter, Random = () => 0.5
			};

			var a = new Worker(Options("a"));
			var b = new Worker(Options("b"));

			var definition = new ScheduleDefinition
			{
				Name = "shared-sweep", Expr = "on second=*/30", Handler = "shared"
			};
			a.AddSchedule(definition);
			b.AddSchedule(definition);

			clock.Advance(30_000);
			var first = await a.TickAsync();
			var second = await b.TickAsync();

			Assert.Equal(1, first.Materialized);
			Assert.Equal(0, second.Materialized, "the idempotency key discarded the duplicate");
			Assert.Equal(1, store.All().Count);
		});
	}

	private static void Lifecycle(TestRunner runner)
	{
		runner.Suite("worker: lifecycle");

		runner.TestAsync("start and stop drive the loop without leaking it", async () =>
		{
			var h = NewHarness();
			var ran = new TaskCompletionSource();
			h.Registry.Register("tick", (_, _) => ran.TrySetResult());

			await h.Worker.EnqueueAsync("tick");
			h.Worker.Start();

			// Wait for the loop to actually reach the handler rather than assuming
			// it got there before the stop, which is a scheduling race.
			var finished = await Task.WhenAny(ran.Task, Task.Delay(TimeSpan.FromSeconds(5)));
			await h.Worker.StopAsync();

			Assert.True(finished == ran.Task, "the loop should have run the queued job");
		});
	}
}
