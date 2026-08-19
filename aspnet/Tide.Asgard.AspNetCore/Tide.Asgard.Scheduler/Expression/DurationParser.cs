// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Tide.Asgard.Scheduler.Expression;

public static class DurationParser
{
	private static readonly Dictionary<string, long> UnitMs = new()
	{
		["ms"] = 1,
		["s"] = 1000,
		["m"] = 60_000,
		["h"] = 3_600_000,
		["d"] = 86_400_000
	};

	private static readonly Regex InstantPattern = new(
		@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(\.\d+)?(Z|[+-]\d{2}:\d{2})$",
		RegexOptions.Compiled);

	// Parses compound duration literals such as 30s, 1h30m or 500ms.
	// Units must appear at most once and in descending order of size so that
	// "1h30m" is valid and "30m1h" is not.
	public static long ParseDuration(string text, int offset = 0)
	{
		if (text.Length == 0)
			throw new ScheduleParseException(ScheduleErrorCode.BadDuration, offset, "empty duration");

		long total = 0;
		var i = 0;
		var lastUnitMs = long.MaxValue;
		var matchedAny = false;

		while (i < text.Length)
		{
			var numStart = i;
			while (i < text.Length && text[i] >= '0' && text[i] <= '9') i++;
			if (i == numStart)
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.BadDuration, offset + i, $"expected digits, got '{text[i]}'");
			}
			var value = long.Parse(text.AsSpan(numStart, i - numStart), CultureInfo.InvariantCulture);

			var unitStart = i;
			while (i < text.Length && text[i] >= 'a' && text[i] <= 'z') i++;
			var unit = text[unitStart..i];
			if (unit.Length == 0)
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.BadDuration, offset + unitStart, "missing unit");
			}

			if (!UnitMs.TryGetValue(unit, out var unitMs))
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.BadDuration, offset + unitStart, $"unknown unit '{unit}'");
			}
			if (unitMs >= lastUnitMs)
			{
				throw new ScheduleParseException(
					ScheduleErrorCode.BadDuration, offset + unitStart,
					$"unit '{unit}' out of order or repeated");
			}

			lastUnitMs = unitMs;
			total += value * unitMs;
			matchedAny = true;
		}

		if (!matchedAny)
			throw new ScheduleParseException(ScheduleErrorCode.BadDuration, offset, "empty duration");
		if (total <= 0)
			throw new ScheduleParseException(ScheduleErrorCode.BadDuration, offset, "duration must be positive");

		return total;
	}

	// ISO 8601 instant. Require an explicit zone designator so that ambient
	// local time can never leak into a stored schedule.
	public static long ParseInstant(string text, int offset = 0)
	{
		if (!InstantPattern.IsMatch(text))
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.BadInstant, offset, $"expected ISO 8601 instant, got '{text}'");
		}

		if (!DateTimeOffset.TryParse(
			text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
		{
			throw new ScheduleParseException(
				ScheduleErrorCode.BadInstant, offset, $"unparseable instant '{text}'");
		}

		return parsed.ToUnixTimeMilliseconds();
	}
}
