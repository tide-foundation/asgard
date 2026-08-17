// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tide.Asgard.Scheduler.Expression;

namespace Tide.Asgard.Scheduler.Tests;

// Mirrors tests/ts/serialization.test.js case for case.
internal static class SerializationTests
{
	public static void Run(TestRunner runner, string fixturePath)
	{
		using var document = JsonDocument.Parse(File.ReadAllText(fixturePath));

		// The property that matters: a spec that has been through storage behaves
		// exactly like one straight from the parser. Reusing the conformance
		// fixtures exercises it across every expression shape rather than a hand
		// picked few.
		runner.Suite("round trip preserves behaviour");
		foreach (var fixture in document.RootElement.GetProperty("sequences").EnumerateArray())
		{
			var name = fixture.GetProperty("name").GetString()!;
			var expr = fixture.GetProperty("expr").GetString()!;
			var after = fixture.GetProperty("after").GetString()!;
			var steps = fixture.GetProperty("expect").GetArrayLength();

			runner.Test(name, () =>
			{
				var direct = ScheduleParser.Parse(expr);
				var restored = ScheduleSpecJson.FromJson(ScheduleSpecJson.ToJson(direct));

				long? a = ParseInstant(after);
				long? b = a;

				for (var i = 0; i < steps; i++)
				{
					a = ScheduleEvaluator.NextFire(direct, a!.Value);
					b = ScheduleEvaluator.NextFire(restored, b!.Value);
					Assert.Equal(a, b, $"{expr} at step {i}");
					if (a is null) break;
				}
			});
		}

		runner.Suite("shape");

		runner.Test("unrestricted fields collapse to any", () =>
		{
			var json = ScheduleSpecJson.ToNode(ScheduleParser.Parse("on hour=3"));
			Assert.Equal("any", json["day"]!.GetValue<string>());
			Assert.Equal("any", json["dow"]!.GetValue<string>());
			Assert.Equal("any", json["month"]!.GetValue<string>());
			Assert.Sequence([3], ToInts(json["hour"]!));
			Assert.Sequence([0], ToInts(json["minute"]!));
		});

		runner.Test("carries a version", () =>
			Assert.Equal(ScheduleSpecJson.SpecVersion,
				ScheduleSpecJson.ToNode(ScheduleParser.Parse("on hour=3"))["v"]!.GetValue<int>()));

		runner.Test("interval keeps mode, anchor and jitter", () =>
		{
			var json = ScheduleSpecJson.ToNode(
				ScheduleParser.Parse("every 15m from 2026-01-01T00:00:00Z jitter 30s"));
			Assert.Equal("interval", json["kind"]!.GetValue<string>());
			Assert.Equal(900_000L, json["periodMs"]!.GetValue<long>());
			Assert.Equal(30_000L, json["jitterMs"]!.GetValue<long>());
			Assert.Equal("fixed_rate", json["mode"]!.GetValue<string>());
			Assert.Equal(ParseInstant("2026-01-01T00:00:00Z"), json["anchorMs"]!.GetValue<long>());
		});

		runner.Test("one shot keeps its instant", () =>
		{
			var json = ScheduleSpecJson.ToNode(ScheduleParser.Parse("at 2026-09-01T03:00:00Z"));
			Assert.Equal("once", json["kind"]!.GetValue<string>());
			Assert.Equal(ParseInstant("2026-09-01T03:00:00Z"), json["atMs"]!.GetValue<long>());
		});

		runner.Test("calendar keeps timezone and dst policies", () =>
		{
			var json = ScheduleSpecJson.ToNode(ScheduleParser.Parse(
				"on 02:30 tz=Australia/Sydney dstgap=skip dstfold=fire_last"));
			Assert.Equal("Australia/Sydney", json["tz"]!.GetValue<string>());
			Assert.Equal("skip", json["dstGap"]!.GetValue<string>());
			Assert.Equal("fire_last", json["dstFold"]!.GetValue<string>());
		});

		runner.Suite("rejects bad stored specs");

		runner.Test("wrong version", () =>
		{
			var json = ScheduleSpecJson.ToNode(ScheduleParser.Parse("on hour=3"));
			json["v"] = 999;
			Assert.Equal(ScheduleErrorCode.BadSpec,
				Assert.Throws(() => ScheduleSpecJson.FromNode(json)).Code);
		});

		runner.Test("unknown kind", () =>
		{
			var json = new JsonObject { ["kind"] = "weekly", ["v"] = ScheduleSpecJson.SpecVersion };
			Assert.Equal(ScheduleErrorCode.BadSpec,
				Assert.Throws(() => ScheduleSpecJson.FromNode(json)).Code);
		});

		runner.Test("field value out of range", () =>
		{
			var json = ScheduleSpecJson.ToNode(ScheduleParser.Parse("on hour=3"));
			json["hour"] = new JsonArray(99);
			Assert.Equal(ScheduleErrorCode.BadSpec,
				Assert.Throws(() => ScheduleSpecJson.FromNode(json)).Code);
		});

		runner.Test("empty field array", () =>
		{
			var json = ScheduleSpecJson.ToNode(ScheduleParser.Parse("on hour=3"));
			json["hour"] = new JsonArray();
			Assert.Equal(ScheduleErrorCode.BadSpec,
				Assert.Throws(() => ScheduleSpecJson.FromNode(json)).Code);
		});

		runner.Test("unknown timezone", () =>
		{
			var json = ScheduleSpecJson.ToNode(ScheduleParser.Parse("on hour=3"));
			json["tz"] = "Mars/Olympus";
			Assert.Equal(ScheduleErrorCode.BadSpec,
				Assert.Throws(() => ScheduleSpecJson.FromNode(json)).Code);
		});

		runner.Test("not json", () =>
			Assert.Equal(ScheduleErrorCode.BadSpec,
				Assert.Throws(() => ScheduleSpecJson.FromJson("{nope")).Code));
	}

	private static List<int> ToInts(JsonNode node)
		=> node.AsArray().Select(v => v!.GetValue<int>()).ToList();

	private static long ParseInstant(string iso) => DateTimeOffset
		.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
		.ToUnixTimeMilliseconds();
}
