// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Runs the shared schedule expression fixtures against the .NET implementation.
// The TypeScript runner in tests/ts reads the same file. A disagreement between
// the two is a bug in one binding, not a difference of opinion.
//
// Usage: dotnet run [path-to-schedule-expression.json]

using System.Globalization;
using System.Text.Json;
using Tide.Asgard.Scheduler.Expression;

var fixturePath = args.Length > 0 ? args[0] : LocateFixtures();
if (fixturePath is null)
{
	Console.Error.WriteLine("could not locate tests/fixtures/schedule-expression.json");
	return 2;
}

using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
var root = document.RootElement;

var failures = new List<string>();
var passed = 0;

foreach (var test in root.GetProperty("sequences").EnumerateArray())
{
	var name = test.GetProperty("name").GetString()!;
	var expr = test.GetProperty("expr").GetString()!;
	var after = test.GetProperty("after").GetString()!;
	var expect = test.GetProperty("expect").EnumerateArray()
		.Select(e => e.ValueKind == JsonValueKind.Null ? null : e.GetString())
		.ToList();

	try
	{
		var spec = ScheduleParser.Parse(expr);
		var cursor = DateTimeOffset.Parse(
			after, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUnixTimeMilliseconds();

		var actual = new List<string?>();
		for (var i = 0; i < expect.Count; i++)
		{
			var fire = ScheduleEvaluator.NextFire(spec, cursor);
			if (fire is null) { actual.Add(null); break; }
			actual.Add(Iso(fire.Value));
			cursor = fire.Value;
		}

		if (!expect.SequenceEqual(actual))
		{
			failures.Add(
				$"sequence \"{name}\" [{expr}]\n    expected {Render(expect)}\n    actual   {Render(actual)}");
		}
		else
		{
			passed++;
		}
	}
	catch (Exception e)
	{
		failures.Add($"sequence \"{name}\" [{expr}]\n    threw {e.Message}");
	}
}

foreach (var test in root.GetProperty("errors").EnumerateArray())
{
	var name = test.GetProperty("name").GetString()!;
	var expr = test.GetProperty("expr").GetString()!;
	var code = test.GetProperty("code").GetString()!;
	var offset = test.GetProperty("offset").GetInt32();

	ScheduleParseException? thrown = null;
	try
	{
		ScheduleParser.Parse(expr);
	}
	catch (ScheduleParseException e)
	{
		thrown = e;
	}

	if (thrown is null)
		failures.Add($"error \"{name}\" [{expr}]\n    expected {code}, parsed successfully");
	else if (thrown.Code != code)
		failures.Add($"error \"{name}\" [{expr}]\n    expected {code}, got {thrown.Code} ({thrown.Message})");
	else if (thrown.Offset != offset)
		failures.Add($"error \"{name}\" [{expr}]\n    {code} expected at offset {offset}, got {thrown.Offset}");
	else
		passed++;
}

var total = root.GetProperty("sequences").GetArrayLength() + root.GetProperty("errors").GetArrayLength();

if (failures.Count > 0)
{
	Console.Error.WriteLine($"FAIL {failures.Count}/{total}\n");
	foreach (var f in failures) Console.Error.WriteLine("  " + f + "\n");
	return 1;
}

Console.WriteLine($"PASS {passed}/{total} schedule expression fixtures");
return 0;

static string Iso(long ms)
{
	var value = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToUniversalTime();
	var format = ms % 1000 == 0 ? "yyyy-MM-ddTHH:mm:ss'Z'" : "yyyy-MM-ddTHH:mm:ss.fff'Z'";
	return value.ToString(format, CultureInfo.InvariantCulture);
}

static string Render(IEnumerable<string?> items)
	=> "[" + string.Join(", ", items.Select(i => i is null ? "null" : $"\"{i}\"")) + "]";

// Walk up from the build output until the repository root comes into view.
static string? LocateFixtures()
{
	var dir = new DirectoryInfo(AppContext.BaseDirectory);
	while (dir is not null)
	{
		var candidate = Path.Combine(dir.FullName, "tests", "fixtures", "schedule-expression.json");
		if (File.Exists(candidate)) return candidate;
		dir = dir.Parent;
	}
	return null;
}
