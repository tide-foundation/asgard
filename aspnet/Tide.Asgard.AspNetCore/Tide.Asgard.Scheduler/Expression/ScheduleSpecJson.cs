// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tide.Asgard.Scheduler.Expression;

// Schedules are stored as this canonical form rather than as their original
// text, and are never re-parsed at fire time. That way a later change to the
// expression language cannot reinterpret a schedule that is already running.
// Keep the original text alongside it for display only.
public static class ScheduleSpecJson
{
	public const int SpecVersion = 1;

	public static JsonObject ToNode(ScheduleSpec spec) => spec switch
	{
		OnceSpec once => new JsonObject
		{
			["kind"] = "once",
			["v"] = SpecVersion,
			["atMs"] = once.AtMs
		},

		IntervalSpec interval => new JsonObject
		{
			["kind"] = "interval",
			["v"] = SpecVersion,
			["periodMs"] = interval.PeriodMs,
			["anchorMs"] = interval.AnchorMs is null ? null : JsonValue.Create(interval.AnchorMs.Value),
			["jitterMs"] = interval.JitterMs,
			["mode"] = ModeToString(interval.Mode)
		},

		CalendarSpec calendar => new JsonObject
		{
			["kind"] = "calendar",
			["v"] = SpecVersion,
			["tz"] = calendar.TimeZoneId,
			["second"] = FieldToNode(calendar.Second),
			["minute"] = FieldToNode(calendar.Minute),
			["hour"] = FieldToNode(calendar.Hour),
			["day"] = FieldToNode(calendar.Day),
			["dayLast"] = calendar.DayLast,
			["dow"] = FieldToNode(calendar.Dow),
			["nth"] = calendar.Nth is null ? null : JsonValue.Create(calendar.Nth.Value),
			["month"] = FieldToNode(calendar.Month),
			["dstGap"] = GapToString(calendar.DstGap),
			["dstFold"] = FoldToString(calendar.DstFold)
		},

		_ => throw BadSpec($"unsupported spec {spec.GetType().Name}")
	};

	public static ScheduleSpec FromNode(JsonNode? node)
	{
		if (node is not JsonObject obj) throw BadSpec("spec must be a JSON object");

		var version = obj["v"]?.GetValue<int>();
		if (version != SpecVersion)
		{
			throw BadSpec($"unsupported spec version {version?.ToString() ?? "null"}, expected {SpecVersion}");
		}

		var kind = obj["kind"]?.GetValue<string>();
		switch (kind)
		{
			case "once":
				return new OnceSpec { AtMs = RequireLong(obj, "atMs") };

			case "interval":
				return new IntervalSpec
				{
					PeriodMs = RequireLong(obj, "periodMs"),
					AnchorMs = OptionalLong(obj, "anchorMs"),
					JitterMs = RequireLong(obj, "jitterMs"),
					Mode = ModeFromString(RequireString(obj, "mode"))
				};

			case "calendar":
			{
				var tzId = RequireString(obj, "tz");
				TimeZoneInfo tz;
				try
				{
					tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
				}
				catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
				{
					throw BadSpec($"unknown timezone '{tzId}'");
				}

				return new CalendarSpec
				{
					TimeZoneId = tzId,
					TimeZone = tz,
					Second = FieldFromNode(obj, "second", FieldRanges.SecondMin, FieldRanges.SecondMax),
					Minute = FieldFromNode(obj, "minute", FieldRanges.MinuteMin, FieldRanges.MinuteMax),
					Hour = FieldFromNode(obj, "hour", FieldRanges.HourMin, FieldRanges.HourMax),
					Day = FieldFromNode(obj, "day", FieldRanges.DayMin, FieldRanges.DayMax),
					DayLast = RequireBool(obj, "dayLast"),
					Dow = FieldFromNode(obj, "dow", FieldRanges.DowMin, FieldRanges.DowMax),
					Nth = OptionalLong(obj, "nth") is { } n ? (int)n : null,
					Month = FieldFromNode(obj, "month", FieldRanges.MonthMin, FieldRanges.MonthMax),
					DstGap = GapFromString(RequireString(obj, "dstGap")),
					DstFold = FoldFromString(RequireString(obj, "dstFold"))
				};
			}

			default:
				throw BadSpec($"unknown spec kind '{kind ?? "null"}'");
		}
	}

	public static string ToJson(ScheduleSpec spec) => ToNode(spec).ToJsonString();

	public static ScheduleSpec FromJson(string text)
	{
		JsonNode? node;
		try
		{
			node = JsonNode.Parse(text);
		}
		catch (JsonException)
		{
			throw BadSpec("spec is not valid JSON");
		}
		return FromNode(node);
	}

	// Unrestricted fields serialize as "any" rather than every value in range,
	// which keeps stored specs readable and small.
	private static JsonNode FieldToNode(FieldSet field)
	{
		if (field.IsAny) return JsonValue.Create("any")!;

		var array = new JsonArray();
		foreach (var v in field.Values) array.Add(v);
		return array;
	}

	private static FieldSet FieldFromNode(JsonObject obj, string name, int min, int max)
	{
		var node = obj[name] ?? throw BadSpec($"{name} is missing");

		if (node is JsonValue value && value.TryGetValue<string>(out var text))
		{
			if (text == "any") return FieldSet.Any(min, max);
			throw BadSpec($"{name} must be \"any\" or a non empty array");
		}

		if (node is not JsonArray array || array.Count == 0)
		{
			throw BadSpec($"{name} must be \"any\" or a non empty array");
		}

		var values = new List<int>(array.Count);
		foreach (var entry in array)
		{
			if (entry is not JsonValue v || !v.TryGetValue<int>(out var parsed) || parsed < min || parsed > max)
			{
				throw BadSpec($"{name} value {entry?.ToJsonString() ?? "null"} is outside {min}..{max}");
			}
			values.Add(parsed);
		}
		return FieldSet.Of(min, max, values);
	}

	private static long RequireLong(JsonObject obj, string name)
	{
		var node = obj[name] ?? throw BadSpec($"{name} must be a number");
		if (node is not JsonValue value || !value.TryGetValue<long>(out var parsed))
		{
			throw BadSpec($"{name} must be a number");
		}
		return parsed;
	}

	private static long? OptionalLong(JsonObject obj, string name)
	{
		var node = obj[name];
		if (node is null) return null;
		if (node is not JsonValue value || !value.TryGetValue<long>(out var parsed))
		{
			throw BadSpec($"{name} must be a number or null");
		}
		return parsed;
	}

	private static string RequireString(JsonObject obj, string name)
	{
		var node = obj[name];
		if (node is not JsonValue value || !value.TryGetValue<string>(out var parsed))
		{
			throw BadSpec($"{name} must be a string");
		}
		return parsed;
	}

	private static bool RequireBool(JsonObject obj, string name)
	{
		var node = obj[name];
		if (node is not JsonValue value || !value.TryGetValue<bool>(out var parsed))
		{
			throw BadSpec($"{name} must be a boolean");
		}
		return parsed;
	}

	private static string ModeToString(IntervalMode mode) => mode switch
	{
		IntervalMode.FixedDelay => "fixed_delay",
		IntervalMode.FixedRate => "fixed_rate",
		_ => throw BadSpec($"unsupported mode {mode}")
	};

	private static IntervalMode ModeFromString(string text) => text switch
	{
		"fixed_delay" => IntervalMode.FixedDelay,
		"fixed_rate" => IntervalMode.FixedRate,
		_ => throw BadSpec($"mode must be one of fixed_delay, fixed_rate, got '{text}'")
	};

	private static string GapToString(DstGapPolicy policy) => policy switch
	{
		DstGapPolicy.FireAtGapEnd => "fire_at_gap_end",
		DstGapPolicy.Skip => "skip",
		_ => throw BadSpec($"unsupported dstGap {policy}")
	};

	private static DstGapPolicy GapFromString(string text) => text switch
	{
		"fire_at_gap_end" => DstGapPolicy.FireAtGapEnd,
		"skip" => DstGapPolicy.Skip,
		_ => throw BadSpec($"dstGap must be one of fire_at_gap_end, skip, got '{text}'")
	};

	private static string FoldToString(DstFoldPolicy policy) => policy switch
	{
		DstFoldPolicy.FireFirst => "fire_first",
		DstFoldPolicy.FireLast => "fire_last",
		_ => throw BadSpec($"unsupported dstFold {policy}")
	};

	private static DstFoldPolicy FoldFromString(string text) => text switch
	{
		"fire_first" => DstFoldPolicy.FireFirst,
		"fire_last" => DstFoldPolicy.FireLast,
		_ => throw BadSpec($"dstFold must be one of fire_first, fire_last, got '{text}'")
	};

	private static ScheduleParseException BadSpec(string detail)
		=> new(ScheduleErrorCode.BadSpec, 0, detail);
}
