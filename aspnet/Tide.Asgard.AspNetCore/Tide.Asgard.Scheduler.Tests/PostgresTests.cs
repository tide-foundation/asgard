// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using Tide.Asgard.Scheduler.Execution;
using Tide.Asgard.Scheduler.Expression;
using Tide.Asgard.Scheduler.Postgres;

namespace Tide.Asgard.Scheduler.Tests;

// Integration tests for the Postgres store. They need a real database because
// the properties that matter, SKIP LOCKED and single statement atomicity, only
// exist in Postgres and cannot be faked.
//
//   SCHEDULER_TEST_DATABASE_URL=postgres://user:pass@host:port/db dotnet run
//
// Without that variable everything except the schema drift check is skipped, so
// the default suite stays runnable with no database.
//
// Mirrors tests/ts/postgres.test.js case for case.
internal static class PostgresTests
{
	private static readonly long T0 = DateTimeOffset
		.Parse("2026-08-17T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
		.ToUnixTimeMilliseconds();

	private static NpgsqlDataSource _db = null!;

	private static readonly RetryPolicy NoJitter = new()
	{
		MaxAttempts = 3, BaseMs = 1_000, CapMs = 60_000, Multiplier = 2, Jitter = JitterMode.None
	};

	public static void Run(TestRunner runner, string repositoryRoot)
	{
		runner.Suite("migrations stay in step with sql/migrations");

		var migrationDir = Path.Combine(repositoryRoot, "sql", "migrations");

		runner.Test("every file on disk is embedded, and nothing extra is", () =>
		{
			var onDisk = Directory.GetFiles(migrationDir, "*.sql")
				.Select(Path.GetFileName)
				.OrderBy(f => f, StringComparer.Ordinal)
				.ToList();

			Assert.Sequence(onDisk!, SchedulerMigrations.All.Select(m => $"{m.Name}.sql"));
		});

		foreach (var migration in SchedulerMigrations.All)
		{
			runner.Test($"{migration.Name} matches its file", () =>
			{
				var canonical = File.ReadAllText(Path.Combine(migrationDir, $"{migration.Name}.sql"));
				Assert.Equal(Normalize(canonical), Normalize(migration.Sql));
			});
		}

		runner.Test("versions start at 1 and increase by one", () =>
			Assert.Sequence(
				Enumerable.Range(1, SchedulerMigrations.All.Count),
				SchedulerMigrations.All.Select(m => m.Version)));

		runner.Test("a file's number matches the version it declares", () =>
		{
			foreach (var migration in SchedulerMigrations.All)
			{
				Assert.Equal(
					int.Parse(migration.Name.Split('-')[0]), migration.Version, migration.Name);
			}
		});

		var url = Environment.GetEnvironmentVariable("SCHEDULER_TEST_DATABASE_URL");
		if (string.IsNullOrWhiteSpace(url))
		{
			Console.WriteLine("SKIP postgres integration tests, SCHEDULER_TEST_DATABASE_URL is not set");
			return;
		}

		// The test owns the data source so it can also run the admin statements
		// that reset state between cases. This is the constructor a host uses when
		// the application already has a pool, rather than PostgresJobStore.Create.
		using var db = NpgsqlDataSource.Create(ToNpgsqlConnectionString(url));
		_db = db;

		var store = new PostgresJobStore(db);
		store.EnsureSchemaAsync().GetAwaiter().GetResult();

		Store(runner, store);
		Schedules(runner, new PostgresScheduleStore(db));
		WorkerOnPostgres(runner, store);
		Notifier(runner, db);
		Migrating(runner, db);
	}

	private static void Store(TestRunner runner, PostgresJobStore store)
	{
		runner.Suite("postgres store");

		runner.TestAsync("EnsureSchemaAsync can run again over an existing schema", async () =>
		{
			await store.EnsureSchemaAsync();
			await store.EnsureSchemaAsync();
		});

		runner.TestAsync("enqueue then claim leases the run and counts the attempt", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0, MaxAttempts = 3 });

			var claimed = await store.ClaimDueAsync("worker-a", T0, 30_000, 10);
			Assert.Equal(1, claimed.Count);
			Assert.Equal(JobStatus.Leased, claimed[0].Status);
			Assert.Equal(1, claimed[0].Attempt);
			Assert.Equal("worker-a", claimed[0].LeaseOwner);
			Assert.Equal(T0 + 30_000, claimed[0].LeaseExpiresAtMs);
			Assert.Equal(3, claimed[0].MaxAttempts);
		});

		runner.TestAsync("payload round trips through jsonb", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest
			{
				Handler = "h",
				RunAtMs = T0,
				Payload = new Dictionary<string, object> { ["realmId"] = "tide", ["retries"] = 2 }
			});

			var claimed = await store.ClaimDueAsync("worker-a", T0, 30_000, 10);
			var payload = claimed[0].Payload as JsonNode;
			Assert.True(payload is not null, "payload should come back as JSON");
			Assert.Equal("tide", payload!["realmId"]!.GetValue<string>());
			Assert.Equal(2, payload["retries"]!.GetValue<int>());
		});

		runner.TestAsync("a null payload stays null", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });

			var claimed = await store.ClaimDueAsync("worker-a", T0, 30_000, 10);
			Assert.True(claimed[0].Payload is null, "payload should be null");
		});

		runner.TestAsync("runs in the future are not claimed", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 + 5_000 });

			Assert.Equal(0, (await store.ClaimDueAsync("worker-a", T0, 30_000, 10)).Count);
			Assert.Equal(1, (await store.ClaimDueAsync("worker-a", T0 + 5_000, 30_000, 10)).Count);
		});

		runner.TestAsync("a repeated idempotency key is discarded", async () =>
		{
			await Reset();
			var request = new JobRunRequest { Handler = "h", RunAtMs = T0, IdempotencyKey = "nightly:1" };

			Assert.True(await store.EnqueueAsync(request) is not null, "first insert should win");
			Assert.True(await store.EnqueueAsync(request) is null, "second insert should be discarded");
			Assert.Equal(1, await CountAll());
		});

		runner.TestAsync("null idempotency keys never collide with each other", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });

			Assert.Equal(2, await CountAll());
		});

		// The property the whole design rests on. Without SKIP LOCKED these
		// workers would either block on each other or hand the same run out twice.
		runner.TestAsync("concurrent claimers never receive the same run", async () =>
		{
			await Reset();
			const int total = 200;
			for (var i = 0; i < total; i++)
			{
				await store.EnqueueAsync(new JobRunRequest
				{
					Handler = "h", RunAtMs = T0, IdempotencyKey = $"job:{i}"
				});
			}

			var batches = await Task.WhenAll(Enumerable.Range(0, 8)
				.Select(i => store.ClaimDueAsync($"worker-{i}", T0, 30_000, total)));

			var ids = batches.SelectMany(b => b).Select(r => r.Id).ToList();
			Assert.Equal(total, ids.Count, "every run should have been claimed exactly once");
			Assert.Equal(total, ids.Distinct().Count(), "no run should appear in two batches");
		});

		runner.TestAsync("claim respects the batch limit and takes the oldest first", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 + 2_000, IdempotencyKey = "c" });
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0, IdempotencyKey = "a" });
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 + 1_000, IdempotencyKey = "b" });

			var claimed = await store.ClaimDueAsync("worker-a", T0 + 5_000, 30_000, 2);
			Assert.Sequence(["a", "b"], claimed.Select(r => r.IdempotencyKey!));
		});

		runner.TestAsync("an expired lease returns the run to pending", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.ClaimDueAsync("worker-a", T0, 1_000, 10);

			Assert.Equal(0, await store.ReapExpiredAsync(T0 + 500));
			Assert.Equal(1, await store.ReapExpiredAsync(T0 + 1_001));

			var reclaimed = await store.ClaimDueAsync("worker-b", T0 + 1_001, 30_000, 10);
			Assert.Equal(2, reclaimed[0].Attempt, "the attempt already spent still counts");
			Assert.Equal("lease expired", reclaimed[0].LastError);
		});

		runner.TestAsync("heartbeat pushes the lease out and fails once the run is settled", async () =>
		{
			await Reset();
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.ClaimDueAsync("worker-a", T0, 1_000, 10);

			Assert.Equal(true, await store.HeartbeatAsync(run!.Id, T0 + 60_000));
			Assert.Equal(0, await store.ReapExpiredAsync(T0 + 1_001));

			await store.CompleteAsync(run.Id, null, T0 + 2_000);
			Assert.Equal(false, await store.HeartbeatAsync(run.Id, T0 + 90_000));
		});

		runner.TestAsync("complete settles and chains in one statement", async () =>
		{
			await Reset();
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.ClaimDueAsync("worker-a", T0, 30_000, 10);

			await store.CompleteAsync(run!.Id, new JobRunRequest
			{
				Handler = "h", RunAtMs = T0 + 60_000, ScheduleId = "loop", IdempotencyKey = "loop:2"
			}, T0 + 1_000);

			Assert.Equal(JobStatus.Succeeded, (await store.GetAsync(run.Id))!.Status);

			var chained = await store.ClaimDueAsync("worker-a", T0 + 60_000, 30_000, 10);
			Assert.Equal("loop:2", chained[0].IdempotencyKey);
			Assert.Equal("loop", chained[0].ScheduleId);
		});

		runner.TestAsync("dead letter settles and still chains", async () =>
		{
			await Reset();
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.ClaimDueAsync("worker-a", T0, 30_000, 10);

			await store.DeadLetterAsync(run!.Id, "gave up", new JobRunRequest
			{
				Handler = "h", RunAtMs = T0 + 60_000, IdempotencyKey = "loop:2"
			}, T0);

			var settled = await store.GetAsync(run.Id);
			Assert.Equal(JobStatus.Dead, settled!.Status);
			Assert.Equal("gave up", settled.LastError);
			Assert.Equal(1, (await store.ClaimDueAsync("worker-a", T0 + 60_000, 30_000, 10)).Count,
				"a dead run must not break a recurring schedule");
		});

		runner.TestAsync("chaining a key that already exists is discarded rather than failing", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest
			{
				Handler = "h", RunAtMs = T0 + 60_000, IdempotencyKey = "loop:2"
			});
			var run = await store.EnqueueAsync(new JobRunRequest
			{
				Handler = "h", RunAtMs = T0, IdempotencyKey = "loop:1"
			});
			await store.ClaimDueAsync("worker-a", T0, 30_000, 10);

			await store.CompleteAsync(run!.Id, new JobRunRequest
			{
				Handler = "h", RunAtMs = T0 + 60_000, IdempotencyKey = "loop:2"
			}, T0);

			Assert.Equal(2, await CountAll());
		});

		runner.TestAsync("retry moves the run forward and keeps the error", async () =>
		{
			await Reset();
			var run = await store.EnqueueAsync(new JobRunRequest
			{
				Handler = "h", RunAtMs = T0, MaxAttempts = 3
			});
			await store.ClaimDueAsync("worker-a", T0, 30_000, 10);

			await store.RetryAsync(run!.Id, "upstream down", T0 + 5_000, T0);

			var pending = await store.GetAsync(run.Id);
			Assert.Equal(JobStatus.Pending, pending!.Status);
			Assert.Equal(T0 + 5_000, pending.RunAtMs);
			Assert.Equal("upstream down", pending.LastError);
			Assert.True(pending.LeaseOwner is null, "lease owner should be cleared");
		});

		runner.TestAsync("get returns null for a run that does not exist", async () =>
		{
			await Reset();
			Assert.True(await store.GetAsync("999999") is null, "expected null");
		});

		runner.TestAsync("claiming can be limited to a set of handlers", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "known", RunAtMs = T0, IdempotencyKey = "a" });
			await store.EnqueueAsync(new JobRunRequest { Handler = "other", RunAtMs = T0, IdempotencyKey = "b" });

			var claimed = await store.ClaimDueAsync("worker-a", T0, 30_000, 10, ["known"]);
			Assert.Sequence(["known"], claimed.Select(r => r.Handler));
			Assert.Equal(1, (await store.StatsAsync(T0)).Pending,
				"the other handler's run is left for someone else");
		});

		runner.TestAsync("an empty handler list claims nothing rather than everything", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "known", RunAtMs = T0 });
			Assert.Equal(0, (await store.ClaimDueAsync("worker-a", T0, 30_000, 10, [])).Count);
		});

		runner.TestAsync("omitting the handler list claims everything", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "known", RunAtMs = T0, IdempotencyKey = "a" });
			await store.EnqueueAsync(new JobRunRequest { Handler = "other", RunAtMs = T0, IdempotencyKey = "b" });

			Assert.Equal(2, (await store.ClaimDueAsync("worker-a", T0, 30_000, 10)).Count);
		});

		runner.TestAsync("purge deletes settled runs past the cutoff and keeps the rest", async () =>
		{
			await Reset();
			var old = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0, IdempotencyKey = "old" });
			var recent = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0, IdempotencyKey = "recent" });
			var pending = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0, IdempotencyKey = "pending" });

			await store.CompleteAsync(old!.Id, null, T0);
			await store.CompleteAsync(recent!.Id, null, T0 + 10_000);

			Assert.Equal(1, await store.PurgeSettledAsync(T0 + 5_000, 100));
			Assert.True(await store.GetAsync(old.Id) is null, "the old run should be gone");
			Assert.True(await store.GetAsync(recent.Id) is not null, "the recent run should remain");
			Assert.True(await store.GetAsync(pending!.Id) is not null, "the pending run should remain");
		});

		runner.TestAsync("purge keeps dead runs unless asked for", async () =>
		{
			await Reset();
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.DeadLetterAsync(run!.Id, "gave up", null, T0);

			Assert.Equal(0, await store.PurgeSettledAsync(T0 + 5_000, 100));
			Assert.Equal(1, await store.PurgeSettledAsync(T0 + 5_000, 100, includeDead: true));
		});

		runner.TestAsync("purge honours the batch limit", async () =>
		{
			await Reset();
			for (var i = 0; i < 5; i++)
			{
				var run = await store.EnqueueAsync(new JobRunRequest
				{
					Handler = "h", RunAtMs = T0, IdempotencyKey = $"k{i}"
				});
				await store.CompleteAsync(run!.Id, null, T0);
			}

			Assert.Equal(2, await store.PurgeSettledAsync(T0 + 1, 2));
			Assert.Equal(2, await store.PurgeSettledAsync(T0 + 1, 2));
			Assert.Equal(1, await store.PurgeSettledAsync(T0 + 1, 2));
			Assert.Equal(0, await store.PurgeSettledAsync(T0 + 1, 2));
		});

		runner.TestAsync("stats counts by status and reports the oldest waiting run", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0, IdempotencyKey = "a" });
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 + 5_000, IdempotencyKey = "b" });
			var done = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0, IdempotencyKey = "c" });
			await store.CompleteAsync(done!.Id, null, T0);

			var stats = await store.StatsAsync(T0 + 10_000);
			Assert.Equal(2, stats.Pending);
			Assert.Equal(1, stats.Succeeded);
			Assert.Equal(0, stats.Dead);
			Assert.Equal(10_000L, stats.OldestPendingAgeMs);
		});

		runner.TestAsync("stats does not count a run that is not due yet as waiting", async () =>
		{
			await Reset();
			await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 + 60_000 });

			var stats = await store.StatsAsync(T0);
			Assert.Equal(1, stats.Pending);
			Assert.Equal(0L, stats.OldestPendingAgeMs);
		});

		runner.TestAsync("stats on an empty table reports zeros", async () =>
		{
			await Reset();
			Assert.Equal(new JobStoreStats(0, 0, 0, 0, 0, 0), await store.StatsAsync(T0));
		});

		runner.TestAsync("cancel stops a pending run and refuses a settled one", async () =>
		{
			await Reset();
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });

			Assert.Equal(true, await store.CancelAsync(run!.Id, T0));
			Assert.Equal(JobStatus.Cancelled, (await store.GetAsync(run.Id))!.Status);
			Assert.Equal(false, await store.CancelAsync(run.Id, T0));
		});

		runner.TestAsync("cancel works on a leased run too", async () =>
		{
			await Reset();
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			await store.ClaimDueAsync("worker-a", T0, 30_000, 10);

			Assert.Equal(true, await store.CancelAsync(run!.Id, T0));
			Assert.True((await store.GetAsync(run.Id))!.LeaseOwner is null, "lease should be cleared");
		});

		runner.TestAsync("requeue revives a dead run with attempts reset", async () =>
		{
			await Reset();
			var run = await store.EnqueueAsync(
				new JobRunRequest { Handler = "h", RunAtMs = T0, MaxAttempts = 3 });
			await store.ClaimDueAsync("worker-a", T0, 30_000, 10);
			await store.DeadLetterAsync(run!.Id, "gave up", null, T0);

			Assert.Equal(true, await store.RequeueAsync(run.Id, T0 + 5_000, T0));

			var revived = await store.GetAsync(run.Id);
			Assert.Equal(JobStatus.Pending, revived!.Status);
			Assert.Equal(0, revived.Attempt);
			Assert.True(revived.LastError is null, "the error should be cleared");
			Assert.Equal(T0 + 5_000, revived.RunAtMs);
		});

		runner.TestAsync("requeue refuses a run that is not settled", async () =>
		{
			await Reset();
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			Assert.Equal(false, await store.RequeueAsync(run!.Id, T0, T0));
		});
	}

	private static void Schedules(TestRunner runner, PostgresScheduleStore schedules)
	{
		runner.Suite("postgres schedule store");

		Task<ScheduleRecord> Upsert(
			string name = "nightly",
			string expr = "on 03:00",
			object? payload = null,
			long? nextFireAtMs = null)
			=> schedules.UpsertAsync(new ScheduleUpsert
			{
				Name = name,
				Handler = "sweep",
				Payload = payload ?? new Dictionary<string, object> { ["realmId"] = "tide" },
				Expr = expr,
				Spec = ScheduleParser.Parse(expr),
				Misfire = MisfirePolicy.FireOnce,
				MaxAttempts = 3,
				NextFireAtMs = nextFireAtMs ?? T0 + 10_000
			}, T0);

		runner.TestAsync("upsert inserts and reads back every field", async () =>
		{
			await Reset();
			var record = await Upsert();

			Assert.Equal("nightly", record.Name);
			Assert.Equal("sweep", record.Handler);
			Assert.Equal("on 03:00", record.Expr);
			Assert.Equal(true, record.Enabled);
			Assert.Equal(MisfirePolicy.FireOnce, record.Misfire);
			Assert.Equal(3, record.MaxAttempts);
			Assert.Equal(T0 + 10_000, record.NextFireAtMs);
		});

		runner.TestAsync("the spec survives the round trip and still evaluates", async () =>
		{
			await Reset();
			await Upsert(name: "sydney", expr: "on 02:30 tz=Australia/Sydney");

			var record = await schedules.GetAsync("sydney");
			var calendar = record!.Spec as CalendarSpec;
			Assert.True(calendar is not null, "expected a calendar spec");
			Assert.Equal("Australia/Sydney", calendar!.TimeZoneId);
			Assert.Sequence([2], calendar.Hour.Values);
		});

		runner.TestAsync("re-registering keeps enabled and the next fire time", async () =>
		{
			await Reset();
			await Upsert();
			await schedules.SetEnabledAsync("nightly", false, T0);
			await schedules.AdvanceAsync("nightly", T0 + 99_000, T0, T0);

			await Upsert(payload: new Dictionary<string, object> { ["realmId"] = "changed" });

			var record = await schedules.GetAsync("nightly");
			Assert.Equal(false, record!.Enabled, "a redeploy must not silently resume it");
			Assert.Equal(T0 + 99_000, record.NextFireAtMs, "and must not move it in time");
		});

		runner.TestAsync("changing the spec does reset the next fire time", async () =>
		{
			await Reset();
			await Upsert();
			await schedules.AdvanceAsync("nightly", T0 + 99_000, T0, T0);

			await Upsert(expr: "on 04:00", nextFireAtMs: T0 + 20_000);

			Assert.Equal(T0 + 20_000, (await schedules.GetAsync("nightly"))!.NextFireAtMs);
		});

		runner.TestAsync("listDue only returns enabled schedules that have come due", async () =>
		{
			await Reset();
			await Upsert(name: "due", nextFireAtMs: T0);
			await Upsert(name: "later", nextFireAtMs: T0 + 60_000);
			await Upsert(name: "paused", nextFireAtMs: T0);
			await schedules.SetEnabledAsync("paused", false, T0);

			var due = await schedules.ListDueAsync(T0, 10);
			Assert.Sequence(["due"], due.Select(x => x.Name));
		});

		runner.TestAsync("a schedule with no next fire time is never due", async () =>
		{
			await Reset();
			await schedules.UpsertAsync(new ScheduleUpsert
			{
				Name = "chained", Handler = "sweep", Expr = "every 10s",
				Spec = ScheduleParser.Parse("every 10s"), NextFireAtMs = null
			}, T0);

			Assert.Equal(0, (await schedules.ListDueAsync(T0 + 1_000_000, 10)).Count);
		});

		runner.TestAsync("advance records where it got to", async () =>
		{
			await Reset();
			await Upsert();
			await schedules.AdvanceAsync("nightly", T0 + 60_000, T0 + 10_000, T0);

			var record = await schedules.GetAsync("nightly");
			Assert.Equal(T0 + 60_000, record!.NextFireAtMs);
			Assert.Equal(T0 + 10_000, record.LastFireAtMs);
		});

		runner.TestAsync("setEnabled and remove report whether they found anything", async () =>
		{
			await Reset();
			await Upsert();

			Assert.Equal(true, await schedules.SetEnabledAsync("nightly", false, T0));
			Assert.Equal(false, await schedules.SetEnabledAsync("nope", false, T0));
			Assert.Equal(true, await schedules.RemoveAsync("nightly"));
			Assert.Equal(false, await schedules.RemoveAsync("nightly"));
			Assert.True(await schedules.GetAsync("nightly") is null, "expected null");
		});

		runner.TestAsync("list is ordered by name", async () =>
		{
			await Reset();
			await Upsert(name: "b");
			await Upsert(name: "a");

			Assert.Sequence(["a", "b"], (await schedules.ListAsync()).Select(x => x.Name));
		});
	}

	private static void WorkerOnPostgres(TestRunner runner, PostgresJobStore store)
	{
		runner.Suite("worker on postgres");

		runner.TestAsync("runs a job end to end", async () =>
		{
			await Reset();
			var seen = new List<string>();
			var greet = Job.Define<string>("greet", (payload, _) => seen.Add(payload));

			var worker = NewWorker(store, [greet], new FakeClock(T0), "solo");
			await worker.EnqueueAsync(greet, "asgard");

			Assert.Equal(1, (await worker.TickAsync()).Succeeded);
			Assert.Equal(1, seen.Count);
		});

		runner.TestAsync("retries with backoff then succeeds", async () =>
		{
			await Reset();
			var clock = new FakeClock(T0);
			var attempts = 0;
			var flaky = Job.Define("flaky", _ =>
			{
				attempts++;
				if (attempts < 3) throw new Exception("not yet");
			});

			var worker = NewWorker(store, [flaky], clock, "solo");
			var run = await worker.EnqueueAsync(flaky);

			Assert.Equal(1, (await worker.TickAsync()).Retried);
			Assert.Equal(T0 + 1_000, (await store.GetAsync(run!.Id))!.RunAtMs);

			clock.Advance(1_000);
			Assert.Equal(1, (await worker.TickAsync()).Retried);

			clock.Advance(2_000);
			Assert.Equal(1, (await worker.TickAsync()).Succeeded);
			Assert.Equal(3, attempts);
		});

		runner.TestAsync("two workers sharing a database run each occurrence once", async () =>
		{
			await Reset();
			var clock = new FakeClock(T0);
			var runs = 0;
			var sweep = Job.Define("sweep", _ => Interlocked.Increment(ref runs));

			var a = NewWorker(store, [sweep], clock, "a");
			var b = NewWorker(store, [sweep], clock, "b");

			var definition = ScheduleDefinition.For("shared-sweep", "on second=*/30", sweep);
			await a.AddScheduleAsync(definition);
			await b.AddScheduleAsync(definition);

			clock.Advance(30_000);
			var results = await Task.WhenAll(a.TickAsync(), b.TickAsync());

			Assert.Equal(1, results.Sum(r => r.Materialized), "materialized once");
			Assert.Equal(1, results.Sum(r => r.Claimed), "claimed once");
			Assert.Equal(1, runs, "and executed once");
		});
	}

	private static Worker NewWorker(
		IJobStore store, IReadOnlyList<JobDefinition> jobs, IClock clock, string owner)
		=> new(new WorkerOptions
		{
			Store = store,
			Jobs = jobs,
			Clock = clock,
			Owner = owner,
			LeaseMs = 30_000,
			Retry = NoJitter,
			Random = () => 0.5
		});

	private static void Notifier(TestRunner runner, NpgsqlDataSource db)
	{
		runner.Suite("postgres notifier");

		runner.TestAsync("a notify on one connection wakes a wait on another", async () =>
		{
			await using var notifier = new PostgresNotifier(db);

			// Get the LISTEN registered before announcing.
			var waiting = notifier.WaitAsync(5_000);
			await Task.Delay(200);

			var started = Environment.TickCount64;
			await notifier.NotifyAsync();
			await waiting;

			Assert.True(Environment.TickCount64 - started < 2_000,
				"should not have waited out the timeout");
		});

		runner.TestAsync("a notify from a completely separate connection also wakes it", async () =>
		{
			await using var notifier = new PostgresNotifier(db);

			var waiting = notifier.WaitAsync(5_000);
			await Task.Delay(200);

			// Standing in for another process entirely.
			await using (var other = db.CreateCommand(
				$"select pg_notify('{PostgresNotifier.JobChannel}', '')"))
			{
				await other.ExecuteNonQueryAsync();
			}

			var started = Environment.TickCount64;
			await waiting;
			Assert.True(Environment.TickCount64 - started < 2_000, "should have been woken");
		});

		runner.TestAsync("noise on another channel is ignored", async () =>
		{
			await using var notifier = new PostgresNotifier(db);

			var started = Environment.TickCount64;
			var waiting = notifier.WaitAsync(400);
			await Task.Delay(100);

			await using (var other = db.CreateCommand("select pg_notify('some_other_channel', '')"))
			{
				await other.ExecuteNonQueryAsync();
			}

			await waiting;
			Assert.True(Environment.TickCount64 - started >= 380,
				"an unrelated channel must not shorten the wait");
		});

		runner.TestAsync("wait gives up after the timeout when nothing happens", async () =>
		{
			await using var notifier = new PostgresNotifier(db);

			var started = Environment.TickCount64;
			await notifier.WaitAsync(300);

			Assert.True(Environment.TickCount64 - started >= 280, "should have waited");
		});

		runner.TestAsync("wait returns when the token is cancelled", async () =>
		{
			await using var notifier = new PostgresNotifier(db);
			using var cts = new CancellationTokenSource();

			var waiting = notifier.WaitAsync(5_000, cts.Token);
			await Task.Delay(100);

			await cts.CancelAsync();
			await waiting;
		});
	}

	// Last on purpose: these drop and rebuild the tables, so nothing after them
	// can depend on the state they leave behind.
	private static void Migrating(TestRunner runner, NpgsqlDataSource db)
	{
		runner.Suite("migrating a database");

		async Task Wipe()
		{
			await using var command = db.CreateCommand(
				"drop table if exists asgard_job_runs, asgard_schedules, asgard_schema_migrations cascade");
			await command.ExecuteNonQueryAsync();
		}

		var expected = SchedulerMigrations.All.Select(m => m.Version).ToList();

		runner.TestAsync("a fresh database gets every migration, in order", async () =>
		{
			await Wipe();

			var applied = await SchedulerMigrations.MigrateAsync(db);
			Assert.Sequence(expected, applied);
			Assert.Sequence(applied, await SchedulerMigrations.AppliedAsync(db));
		});

		runner.TestAsync("migrating again does nothing", async () =>
		{
			await Wipe();
			await SchedulerMigrations.MigrateAsync(db);

			Assert.Equal(0, (await SchedulerMigrations.MigrateAsync(db)).Count, "already up to date");
		});

		runner.TestAsync("only the missing ones are applied", async () =>
		{
			await Wipe();
			await SchedulerMigrations.MigrateAsync(db);

			// Forget the last migration, as though this database were one behind.
			var last = SchedulerMigrations.All[^1];
			await using (var forget = db.CreateCommand(
				$"delete from asgard_schema_migrations where version = {last.Version}"))
			{
				await forget.ExecuteNonQueryAsync();
			}

			Assert.Sequence([last.Version], await SchedulerMigrations.MigrateAsync(db));
		});

		runner.TestAsync("each migration is safe to apply twice", async () =>
		{
			await Wipe();
			await SchedulerMigrations.MigrateAsync(db);

			// Replaying every migration against an already migrated database is
			// the situation a crash between applying and recording leaves behind.
			foreach (var migration in SchedulerMigrations.All)
			{
				await using var replay = db.CreateCommand(migration.Sql);
				await replay.ExecuteNonQueryAsync();
			}

			Assert.Sequence(expected, await SchedulerMigrations.AppliedAsync(db));
		});

		runner.TestAsync("concurrent migrators do not trip over each other", async () =>
		{
			await Wipe();

			var results = await Task.WhenAll(Enumerable.Range(0, 5)
				.Select(_ => SchedulerMigrations.MigrateAsync(db)));

			// Exactly one caller does each piece of work, the rest find it done.
			Assert.Equal(expected.Count, results.Sum(r => r.Count),
				"each migration should be applied by exactly one caller");
			Assert.Sequence(expected, await SchedulerMigrations.AppliedAsync(db));
		});

		runner.TestAsync("the schema works after migrating from nothing", async () =>
		{
			await Wipe();
			await SchedulerMigrations.MigrateAsync(db);

			var store = new PostgresJobStore(db);
			var run = await store.EnqueueAsync(new JobRunRequest { Handler = "h", RunAtMs = T0 });
			Assert.Equal(true, await store.CancelAsync(run!.Id, T0), "the cancelled status exists");
		});
	}

	// Every test starts from an empty table so ids and counts are predictable.
	private static async Task Reset()
	{
		await using var command = _db.CreateCommand(
			"truncate table asgard_job_runs restart identity; truncate table asgard_schedules");
		await command.ExecuteNonQueryAsync();
	}

	private static async Task<int> CountAll()
	{
		await using var command = _db.CreateCommand("select count(*) from asgard_job_runs");
		return (int)(long)(await command.ExecuteScalarAsync())!;
	}

	// Comments are allowed to differ, DDL is not.
	private static string Normalize(string sql) => string.Join("\n", sql
		.Split('\n')
		.Select(line =>
		{
			var comment = line.IndexOf("--", StringComparison.Ordinal);
			return (comment >= 0 ? line[..comment] : line).TrimEnd();
		})
		.Where(line => line.Trim().Length > 0));

	// Npgsql takes key value connection strings, not URLs, so the test converts
	// the same variable the TypeScript suite reads.
	private static string ToNpgsqlConnectionString(string url)
	{
		var uri = new Uri(url);
		var credentials = uri.UserInfo.Split(':', 2);
		var port = uri.Port > 0 ? uri.Port : 5432;

		return $"Host={uri.Host};Port={port};Username={credentials[0]};" +
			$"Password={(credentials.Length > 1 ? credentials[1] : string.Empty)};" +
			$"Database={uri.AbsolutePath.TrimStart('/')}";
	}
}
