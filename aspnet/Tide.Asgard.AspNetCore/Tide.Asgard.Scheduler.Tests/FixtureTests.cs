// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Tide.Asgard.Scheduler.Expression;

namespace Tide.Asgard.Scheduler.Tests;

// Runs the shared conformance fixtures. The TypeScript runner in tests/ts reads
// the same file, so a disagreement between the two is a bug in one binding
// rather than a difference of opinion.
internal static class FixtureTests
{
	public static void Run(TestRunner runner, string fixturePath)
	{
		using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));
		var root = document.RootElement;

		runner.Suite("fixtures: fire sequences");
		foreach (var fixture in root.GetProperty("sequences").EnumerateArray())
		{
			var name = fixture.GetProperty("name").GetString()!;
			var expr = fixture.GetProperty("expr").GetString()!;
			var after = fixture.GetProperty("after").GetString()!;
			var expect = fixture.GetProperty("expect").EnumerateArray()
				.Select(e => e.ValueKind == JsonValueKind.Null ? null : e.GetString())
				.ToList();

			runner.Test(name, () =>
			{
				var spec = ScheduleParser.Parse(expr);
				var cursor = ParseInstant(after);
				var actual = new List<string?>();

				for (var i = 0; i < expect.Count; i++)
				{
					var fire = ScheduleEvaluator.NextFire(spec, cursor);
					if (fire is null)
					{
						actual.Add(null);
						break;
					}
					actual.Add(Iso(fire.Value));
					cursor = fire.Value;
				}

				Assert.Sequence(expect, actual, expr);
			});
		}

		runner.Suite("fixtures: parse errors");
		foreach (var fixture in root.GetProperty("errors").EnumerateArray())
		{
			var name = fixture.GetProperty("name").GetString()!;
			var expr = fixture.GetProperty("expr").GetString()!;
			var code = fixture.GetProperty("code").GetString()!;
			var offset = fixture.GetProperty("offset").GetInt32();

			runner.Test(name, () =>
			{
				var thrown = Assert.Throws(() => ScheduleParser.Parse(expr));
				Assert.Equal(code, thrown.Code, $"{expr}: code");
				Assert.Equal(offset, thrown.Offset, $"{expr}: offset");
			});
		}
	}

	private static long ParseInstant(string iso) => DateTimeOffset
		.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
		.ToUnixTimeMilliseconds();

	private static string Iso(long ms)
	{
		var value = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToUniversalTime();
		var format = ms % 1000 == 0 ? "yyyy-MM-ddTHH:mm:ss'Z'" : "yyyy-MM-ddTHH:mm:ss.fff'Z'";
		return value.ToString(format, CultureInfo.InvariantCulture);
	}

	// Walk up from the build output until the repository root comes into view.
	public static string? Locate()
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
}
