// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json.Nodes;
using Npgsql;
using Tide.Asgard.Scheduler.Execution;
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
		runner.Suite("schema stays in step with sql/scheduler-schema.sql");
		runner.Test("the embedded copy matches the canonical file", () =>
		{
			var canonical = File.ReadAllText(Path.Combine(repositoryRoot, "sql", "scheduler-schema.sql"));
			Assert.Equal(Normalize(canonical), Normalize(PostgresJobStore.SchemaSql));
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
		WorkerOnPostgres(runner, store);
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
	}

	private static void WorkerOnPostgres(TestRunner runner, PostgresJobStore store)
	{
		runner.Suite("worker on postgres");

		runner.TestAsync("runs a job end to end", async () =>
		{
			await Reset();
			var registry = new HandlerRegistry();
			var seen = new List<string>();
			registry.Register("greet", (payload, _) => seen.Add(payload?.ToString() ?? "null"));

			var worker = NewWorker(store, registry, new FakeClock(T0), "solo");
			await worker.EnqueueAsync("greet", "asgard");

			Assert.Equal(1, (await worker.TickAsync()).Succeeded);
			Assert.Equal(1, seen.Count);
		});

		runner.TestAsync("retries with backoff then succeeds", async () =>
		{
			await Reset();
			var clock = new FakeClock(T0);
			var registry = new HandlerRegistry();
			var attempts = 0;
			registry.Register("flaky", (_, _) =>
			{
				attempts++;
				if (attempts < 3) throw new Exception("not yet");
			});

			var worker = NewWorker(store, registry, clock, "solo");
			var run = await worker.EnqueueAsync("flaky");

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
			var registry = new HandlerRegistry();
			var runs = 0;
			registry.Register("sweep", (_, _) => Interlocked.Increment(ref runs));

			var a = NewWorker(store, registry, clock, "a");
			var b = NewWorker(store, registry, clock, "b");

			var definition = new ScheduleDefinition
			{
				Name = "shared-sweep", Expr = "on second=*/30", Handler = "sweep"
			};
			a.AddSchedule(definition);
			b.AddSchedule(definition);

			clock.Advance(30_000);
			var results = await Task.WhenAll(a.TickAsync(), b.TickAsync());

			Assert.Equal(1, results.Sum(r => r.Materialized), "materialized once");
			Assert.Equal(1, results.Sum(r => r.Claimed), "claimed once");
			Assert.Equal(1, runs, "and executed once");
		});
	}

	private static Worker NewWorker(
		IJobStore store, HandlerRegistry registry, IClock clock, string owner)
		=> new(new WorkerOptions
		{
			Store = store,
			Registry = registry,
			Clock = clock,
			Owner = owner,
			LeaseMs = 30_000,
			Retry = NoJitter,
			Random = () => 0.5
		});

	// Every test starts from an empty table so ids and counts are predictable.
	private static async Task Reset()
	{
		await using var command = _db.CreateCommand("truncate table asgard_job_runs restart identity");
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
