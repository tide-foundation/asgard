// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Runnable tour of the scheduler expression API.
//
//   dotnet run --project examples/dotnet/SchedulerExample

using System.Globalization;
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

// 3. Rerun a function on a schedule.
//
// In-process only. Nothing survives a restart and two replicas will both fire,
// so use this for local timers rather than for work that must happen once.

Console.WriteLine("\n--- driving a function every second, three times ---");

using var cts = new CancellationTokenSource();
var ticks = 0;

await RunOnSchedule("every 1s", () =>
{
	Console.WriteLine($"  tick {++ticks} at {DateTimeOffset.UtcNow:O}");
	if (ticks == 3) cts.Cancel();
	return Task.CompletedTask;
}, cts.Token);

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

static async Task RunOnSchedule(string expr, Func<Task> work, CancellationToken ct)
{
	var spec = ScheduleParser.Parse(expr);

	while (!ct.IsCancellationRequested)
	{
		var next = ScheduleEvaluator.NextFire(spec, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
		if (next is null) return;

		var delay = next.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		if (delay > 0)
		{
			try { await Task.Delay(TimeSpan.FromMilliseconds(delay), ct); }
			catch (OperationCanceledException) { return; }
		}
		if (ct.IsCancellationRequested) return;
		await work();
	}
}
