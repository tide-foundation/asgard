// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Runnable tour of the scheduler expression API.
//
//   dotnet run --project examples/dotnet/SchedulerExample

using System.Globalization;
using Tide.Asgard.Scheduler.Execution;
using Tide.Asgard.Scheduler.Expression;

// 1. Preview when a schedule will fire.
//
// Parse once and keep the spec. It is immutable, so the same one can drive any
// number of evaluations.

Console.WriteLine("--- upcoming fires ---");
Preview("on 03:00");
Preview("on 09:30 dow=mon,wed,fri");
Preview("on minute=*/15");
Preview("on day=last 23:55");
Preview("on nth=2 dow=tue 10:00");
Preview("on 02:30 tz=Australia/Sydney");
Preview("every 90m");
Preview("at 2030-01-01T00:00:00Z");

// Calendar edge cases resolve rather than throwing.
Console.WriteLine("\n--- calendar edge cases ---");
Preview("on day=29 month=2");     // leap days only
Preview("on day=31");             // skips short months
Preview("on day=30 month=2", 1);  // never fires, and says so

// 2. Bad expressions carry a code and a character offset.

Console.WriteLine("\n--- error reporting ---");
foreach (var bad in new[] { "on hour=25", "on day=1 dow=mon", "every 5x", "on nth=2 hour=10" })
{
	try
	{
		ScheduleParser.Parse(bad);
	}
	catch (ScheduleParseException e)
	{
		Console.WriteLine($"  {bad}");
		Console.WriteLine($"  {new string(' ', e.Offset)}^ {e.Code}");
	}
}

// 3. Actually run jobs.
//
// A worker claims due work from a store and dispatches it to handlers looked up
// by name. InMemoryJobStore keeps everything in the process, so nothing survives
// a restart. Swap in a durable IJobStore and the same worker coordinates across
// replicas.

Console.WriteLine("\n--- running a worker for three seconds ---");
await RunWorker();
Console.WriteLine("stopped");
return 0;

static void Preview(string expr, int count = 3)
{
	var spec = ScheduleParser.Parse(expr);
	var fires = new List<string>();
	var cursor = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	for (var i = 0; i < count; i++)
	{
		var next = ScheduleEvaluator.NextFire(spec, cursor);
		if (next is null)
		{
			fires.Add("never again");
			break;
		}
		cursor = next.Value;
		fires.Add(DateTimeOffset.FromUnixTimeMilliseconds(cursor)
			.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));
	}

	Console.WriteLine($"{expr,-42} {string.Join("  ", fires)}");
}

static async Task RunWorker()
{
	var store = new InMemoryJobStore();
	var registry = new HandlerRegistry();

	registry.Register("heartbeat", (payload, _) =>
		Console.WriteLine($"  heartbeat {payload} at {DateTimeOffset.UtcNow:O}"));

	// Fails twice, then succeeds. The worker backs off between attempts.
	var attempts = 0;
	registry.Register("flaky", (_, ctx) =>
	{
		attempts++;
		Console.WriteLine($"  flaky attempt {ctx.Attempt} of {ctx.MaxAttempts}");
		if (attempts < 3) throw new Exception("upstream not ready");
	});

	// Cannot succeed no matter how many times it runs, so it skips its remaining
	// attempts and goes straight to dead.
	registry.Register("malformed", (_, _) =>
		throw new PermanentJobException("payload is missing a realm id"));

	await using var worker = new Worker(new WorkerOptions
	{
		Store = store,
		Registry = registry,
		Concurrency = 2,
		PollIntervalMs = 100,
		Retry = new RetryPolicy
		{
			MaxAttempts = 4, BaseMs = 200, CapMs = 5_000, Multiplier = 2, Jitter = JitterMode.None
		}
	});

	worker.AddSchedule(new ScheduleDefinition
	{
		Name = "heartbeat-every-second",
		Expr = "every 1s",
		Handler = "heartbeat",
		Payload = "tide"
	});

	await worker.EnqueueAsync("flaky");
	await worker.EnqueueAsync("malformed");

	worker.Start();
	await Task.Delay(TimeSpan.FromSeconds(3));
	await worker.StopAsync();

	Console.WriteLine("\n--- final state ---");
	foreach (var status in new[] { JobStatus.Succeeded, JobStatus.Pending, JobStatus.Dead })
	{
		Console.WriteLine($"  {status,-10} {store.CountByStatus(status)}");
	}
	foreach (var run in store.ByStatus(JobStatus.Dead))
	{
		Console.WriteLine($"  dead: {run.Handler} after {run.Attempt} attempt(s), {run.LastError}");
	}
}
