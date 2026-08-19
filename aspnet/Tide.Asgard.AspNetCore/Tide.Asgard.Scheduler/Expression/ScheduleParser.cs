// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Globalization;

namespace Tide.Asgard.Scheduler.Expression;

public static class ScheduleParser
{
	// Granularity ladder, coarsest first. The defaulting rule keys off this: any
	// field finer than the finest one the user named collapses to its floor value,
	// and any field coarser than it stays unrestricted.
	private const int LevelMonth = 0;
	private const int LevelDay = 1;
	private const int LevelHour = 2;
	private const int LevelMinute = 3;
	private const int LevelSecond = 4;

	private static readonly HashSet<string> KnownFields =
	[
		"second", "minute", "hour", "day", "dow", "month", "nth", "tz", "dstgap", "dstfold"
	];

	private readonly record struct RawField(string Value, int ValueOffset, Token Token);

	public static ScheduleSpec Parse(string input)
	{
		var tokens = Tokenizer.Tokenize(input);
		if (tokens.Count == 0)
			throw new ScheduleParseException(ScheduleErrorCode.Empty, 0, "expression is empty");

		var leader = tokens[0];
		return leader.Text.ToLowerInvariant() switch
		{
			"on" => ParseCalendar(tokens),
			"every" => ParseInterval(tokens),
			"at" => ParseOnce(tokens),
			_ => throw new ScheduleParseException(
				ScheduleErrorCode.UnknownLeader, leader.Offset,
				$"expected 'on', 'every' or 'at', got '{leader.Text}'")
		};
	}

	// at <iso-instant>
	private static OnceSpec ParseOnce(List<Token> tokens)
	{
		if (tokens.Count < 2)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.MissingValue, tokens[0].Offset + tokens[0].Text.Length,
				"'at' requires an instant");
		}
		if (tokens.Count > 2)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.Trailing, tokens[2].Offset, $"unexpected '{tokens[2].Text}'");
		}
		return new OnceSpec { AtMs = DurationParser.ParseInstant(tokens[1].Text, tokens[1].Offset) };
	}

	// every <duration> [from <instant>] [jitter <duration>] [mode=fixed_rate|fixed_delay]
	private static IntervalSpec ParseInterval(List<Token> tokens)
	{
		if (tokens.Count < 2)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.MissingValue, tokens[0].Offset + tokens[0].Text.Length,
				"'every' requires a duration");
		}

		var periodMs = DurationParser.ParseDuration(tokens[1].Text.ToLowerInvariant(), tokens[1].Offset);
		long? anchorMs = null;
		long jitterMs = 0;
		var mode = IntervalMode.FixedDelay;

		var i = 2;
		while (i < tokens.Count)
		{
			var t = tokens[i];
			var lower = t.Text.ToLowerInvariant();

			if (lower == "from")
			{
				var next = RequireNext(tokens, i, "'from'");
				anchorMs = DurationParser.ParseInstant(next.Text, next.Offset);
				// An explicit grid anchor only makes sense on a fixed rate schedule.
				mode = IntervalMode.FixedRate;
				i += 2;
			}
			else if (lower == "jitter")
			{
				var next = RequireNext(tokens, i, "'jitter'");
				jitterMs = DurationParser.ParseDuration(next.Text.ToLowerInvariant(), next.Offset);
				i += 2;
			}
			else if (lower.StartsWith("mode=", StringComparison.Ordinal))
			{
				var raw = lower["mode=".Length..];
				var valueOffset = t.Offset + "mode=".Length;
				mode = raw switch
				{
					"fixed_rate" => IntervalMode.FixedRate,
					"fixed_delay" => IntervalMode.FixedDelay,
					_ => throw new ScheduleParseException(
						ScheduleErrorCode.BadValue, valueOffset,
						$"mode must be fixed_rate or fixed_delay, got '{raw}'")
				};
				i += 1;
			}
			else
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.Trailing, t.Offset, $"unexpected '{t.Text}'");
			}
		}

		if (mode == IntervalMode.FixedDelay && anchorMs is not null)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.BadValue, tokens[0].Offset,
				"'from' anchors a grid and cannot be combined with mode=fixed_delay");
		}

		return new IntervalSpec
		{
			PeriodMs = periodMs,
			AnchorMs = anchorMs,
			JitterMs = jitterMs,
			Mode = mode
		};
	}

	private static Token RequireNext(List<Token> tokens, int i, string what)
	{
		if (i + 1 >= tokens.Count)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.MissingValue, tokens[i].Offset + tokens[i].Text.Length,
				$"{what} requires a value");
		}
		return tokens[i + 1];
	}

	// on <field>=<value> ...
	private static CalendarSpec ParseCalendar(List<Token> tokens)
	{
		var fields = new Dictionary<string, RawField>();

		for (var i = 1; i < tokens.Count; i++)
		{
			var t = tokens[i];
			var eq = t.Text.IndexOf('=');
			if (eq < 0)
			{
				// A bare HH:MM is shorthand for the hour and minute fields, since a
				// daily time is by far the most common schedule.
				if (t.Text.Contains(':'))
				{
					ExpandTimeLiteral(t, fields);
					continue;
				}
				throw new ScheduleParseException(
					ScheduleErrorCode.UnknownField, t.Offset, $"expected name=value, got '{t.Text}'");
			}

			var name = t.Text[..eq].ToLowerInvariant();
			if (eq == t.Text.Length - 1)
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.MissingValue, t.Offset + eq + 1, $"'{name}' has no value");
			}
			if (!KnownFields.Contains(name))
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.UnknownField, t.Offset, $"unknown field '{name}'");
			}
			if (fields.ContainsKey(name))
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.DuplicateField, t.Offset, $"field '{name}' set twice");
			}

			fields[name] = new RawField(t.Text[(eq + 1)..], t.Offset + eq + 1, t);
		}

		var (tzId, tz) = ParseTimeZone(Get(fields, "tz"));
		var dstGap = ParseDstGap(Get(fields, "dstgap"));
		var dstFold = ParseDstFold(Get(fields, "dstfold"));

		var monthRaw = Get(fields, "month");
		var dayRaw = Get(fields, "day");
		var dowRaw = Get(fields, "dow");
		var nthRaw = Get(fields, "nth");
		var hourRaw = Get(fields, "hour");
		var minuteRaw = Get(fields, "minute");
		var secondRaw = Get(fields, "second");

		// Which rungs of the granularity ladder the user actually touched.
		var touched = new[]
		{
			monthRaw is not null,
			dayRaw is not null || dowRaw is not null || nthRaw is not null,
			hourRaw is not null,
			minuteRaw is not null,
			secondRaw is not null
		};

		var finest = -1;
		for (var level = 0; level < touched.Length; level++)
		{
			if (touched[level]) finest = level;
		}
		if (finest < 0)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.Empty, tokens[0].Offset, "'on' requires at least one field");
		}

		var dayLast = false;
		FieldSet day;
		if (dayRaw is not null)
		{
			if (dayRaw.Value.Value.Equals("last", StringComparison.OrdinalIgnoreCase))
			{
				dayLast = true;
				day = FieldSet.Any(FieldRanges.DayMin, FieldRanges.DayMax);
			}
			else
			{
				day = ParseValueList(dayRaw.Value, FieldRanges.DayMin, FieldRanges.DayMax, null);
			}
		}
		else
		{
			day = DefaultFor(LevelDay, finest, FieldRanges.DayMin, FieldRanges.DayMax);
		}

		var dow = dowRaw is not null
			? ParseValueList(dowRaw.Value, FieldRanges.DowMin, FieldRanges.DowMax, FieldRanges.DowNames)
			: FieldSet.Any(FieldRanges.DowMin, FieldRanges.DowMax);

		var month = monthRaw is not null
			? ParseValueList(monthRaw.Value, FieldRanges.MonthMin, FieldRanges.MonthMax, FieldRanges.MonthNames)
			: FieldSet.Any(FieldRanges.MonthMin, FieldRanges.MonthMax);

		var hour = hourRaw is not null
			? ParseValueList(hourRaw.Value, FieldRanges.HourMin, FieldRanges.HourMax, null)
			: DefaultFor(LevelHour, finest, FieldRanges.HourMin, FieldRanges.HourMax);

		var minute = minuteRaw is not null
			? ParseValueList(minuteRaw.Value, FieldRanges.MinuteMin, FieldRanges.MinuteMax, null)
			: DefaultFor(LevelMinute, finest, FieldRanges.MinuteMin, FieldRanges.MinuteMax);

		var second = secondRaw is not null
			? ParseValueList(secondRaw.Value, FieldRanges.SecondMin, FieldRanges.SecondMax, null)
			: DefaultFor(LevelSecond, finest, FieldRanges.SecondMin, FieldRanges.SecondMax);

		int? nth = nthRaw is not null ? ParseNth(nthRaw.Value) : null;

		if (nth is not null && dowRaw is null)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.NthWithoutDow, nthRaw!.Value.Token.Offset,
				"nth requires dow to select which weekday to count");
		}

		// Standard cron ORs day and dow when both are restricted, which surprises
		// people. Reject the combination instead of inheriting the ambiguity.
		var dayRestricted = dayLast || !day.IsAny;
		if (dayRestricted && !dow.IsAny)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.DayAmbiguous, (dayRaw ?? dowRaw)!.Value.Token.Offset,
				"day and dow cannot both be restricted");
		}

		return new CalendarSpec
		{
			TimeZoneId = tzId,
			TimeZone = tz,
			Second = second,
			Minute = minute,
			Hour = hour,
			Day = day,
			DayLast = dayLast,
			Dow = dow,
			Nth = nth,
			Month = month,
			DstGap = dstGap,
			DstFold = dstFold
		};
	}

	// Rewrites HH:MM or HH:MM:SS into the fields it stands for, so the rest of the
	// parser and the defaulting rule see no difference between "on 09:30" and
	// "on hour=9 minute=30".
	private static void ExpandTimeLiteral(Token t, Dictionary<string, RawField> fields)
	{
		var parts = t.Text.Split(':');
		if (parts.Length is < 2 or > 3)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.BadValue, t.Offset, $"expected HH:MM or HH:MM:SS, got '{t.Text}'");
		}

		string[] names = ["hour", "minute", "second"];
		var cursor = 0;

		for (var i = 0; i < parts.Length; i++)
		{
			var offset = t.Offset + cursor;
			cursor += parts[i].Length + 1;

			if (parts[i].Length is < 1 or > 2 || !parts[i].All(char.IsAsciiDigit))
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.BadValue, offset, $"'{parts[i]}' is not a valid {names[i]}");
			}
			if (fields.ContainsKey(names[i]))
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.DuplicateField, t.Offset, $"field '{names[i]}' set twice");
			}
			fields[names[i]] = new RawField(parts[i], offset, t);
		}
	}

	private static RawField? Get(Dictionary<string, RawField> fields, string name)
		=> fields.TryGetValue(name, out var f) ? f : null;

	// Fields finer than the finest named one collapse to their floor. Coarser
	// fields stay unrestricted.
	private static FieldSet DefaultFor(int level, int finest, int min, int max)
		=> level > finest ? FieldSet.Single(min, max, min) : FieldSet.Any(min, max);

	private static int ParseNth(RawField raw)
	{
		var lower = raw.Value.ToLowerInvariant();
		if (lower == "last") return -1;
		if (lower.Length != 1 || lower[0] < '1' || lower[0] > '5')
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.ValueRange, raw.ValueOffset,
				$"nth must be 1 to 5 or 'last', got '{raw.Value}'");
		}
		return lower[0] - '0';
	}

	private static (string, TimeZoneInfo) ParseTimeZone(RawField? raw)
	{
		if (raw is null) return ("UTC", TimeZoneInfo.Utc);
		try
		{
			return (raw.Value.Value, TimeZoneInfo.FindSystemTimeZoneById(raw.Value.Value));
		}
		catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.UnknownTimeZone, raw.Value.ValueOffset,
				$"unknown timezone '{raw.Value.Value}'");
		}
	}

	private static DstGapPolicy ParseDstGap(RawField? raw)
	{
		if (raw is null) return DstGapPolicy.FireAtGapEnd;
		return raw.Value.Value.ToLowerInvariant() switch
		{
			"fire_at_gap_end" => DstGapPolicy.FireAtGapEnd,
			"skip" => DstGapPolicy.Skip,
			_ => throw new ScheduleParseException(
				ScheduleErrorCode.BadValue, raw.Value.ValueOffset,
				$"dstgap must be one of fire_at_gap_end, skip, got '{raw.Value.Value}'")
		};
	}

	private static DstFoldPolicy ParseDstFold(RawField? raw)
	{
		if (raw is null) return DstFoldPolicy.FireFirst;
		return raw.Value.Value.ToLowerInvariant() switch
		{
			"fire_first" => DstFoldPolicy.FireFirst,
			"fire_last" => DstFoldPolicy.FireLast,
			_ => throw new ScheduleParseException(
				ScheduleErrorCode.BadValue, raw.Value.ValueOffset,
				$"dstfold must be one of fire_first, fire_last, got '{raw.Value.Value}'")
		};
	}

	// Accepts *, */n, a, a-b, a-b/n, a/n and comma separated combinations.
	private static FieldSet ParseValueList(
		RawField raw, int min, int max, IReadOnlyDictionary<string, int>? names)
	{
		var values = new List<int>();
		var cursor = 0;

		foreach (var part in raw.Value.Split(','))
		{
			var partOffset = raw.ValueOffset + cursor;
			cursor += part.Length + 1;
			if (part.Length == 0)
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.BadValue, partOffset, "empty list entry");
			}
			ParseRangePart(part, partOffset, min, max, names, values);
		}

		return FieldSet.Of(min, max, values);
	}

	private static void ParseRangePart(
		string part, int offset, int min, int max,
		IReadOnlyDictionary<string, int>? names, List<int> outValues)
	{
		var step = 1;
		var body = part;
		var slash = part.IndexOf('/');

		if (slash >= 0)
		{
			body = part[..slash];
			var stepText = part[(slash + 1)..];
			if (stepText.Length == 0 || !stepText.All(char.IsAsciiDigit))
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.BadStep, offset + slash + 1,
					$"step must be a number, got '{stepText}'");
			}
			step = int.Parse(stepText, CultureInfo.InvariantCulture);
			if (step < 1)
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.BadStep, offset + slash + 1, "step must be at least 1");
			}
		}

		int lo;
		int hi;

		if (body == "*")
		{
			lo = min;
			hi = max;
		}
		else
		{
			// Only treat a dash as a range separator when it is not the leading
			// character, so a future signed value cannot be misread.
			var dash = body.IndexOf('-', 1);
			if (dash >= 0)
			{
				lo = ParseScalar(body[..dash], offset, min, max, names);
				hi = ParseScalar(body[(dash + 1)..], offset + dash + 1, min, max, names);
				if (hi < lo)
				{
					throw new ScheduleParseException(
						ScheduleErrorCode.BadRange, offset, $"range '{body}' ends before it starts");
				}
			}
			else
			{
				lo = ParseScalar(body, offset, min, max, names);
				// A bare value with a step runs from that value to the field max,
				// matching how cron reads 10/15.
				hi = slash >= 0 ? max : lo;
			}
		}

		for (var v = lo; v <= hi; v += step) outValues.Add(v);
	}

	private static int ParseScalar(
		string text, int offset, int min, int max, IReadOnlyDictionary<string, int>? names)
	{
		if (text.Length == 0)
			throw new ScheduleParseException(ScheduleErrorCode.BadValue, offset, "empty value");

		int value;
		if (text.All(char.IsAsciiDigit))
		{
			if (!int.TryParse(text, CultureInfo.InvariantCulture, out value))
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.ValueRange, offset, $"'{text}' is outside {min}..{max}");
			}
		}
		else if (names is not null && names.TryGetValue(text.ToLowerInvariant(), out var named))
		{
			value = named;
		}
		else
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.BadValue, offset, $"'{text}' is not a valid value");
		}

		if (value < min || value > max)
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.ValueRange, offset, $"{value} is outside {min}..{max}");
		}
		return value;
	}
}
