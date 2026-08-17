// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Expression;

public enum IntervalMode
{
	// Next fire is measured from the completion of the previous run, so runs
	// can never overlap. This is the default for "rerun this function".
	FixedDelay,
	// Next fire sits on a fixed grid anchored to AnchorMs, regardless of how
	// long a run took.
	FixedRate
}

public enum DstGapPolicy
{
	// Wall clock time does not exist on this day. Fire when the gap ends.
	FireAtGapEnd,
	// Skip the occurrence entirely and look for the next one.
	Skip
}

public enum DstFoldPolicy
{
	// Wall clock time happens twice. Use the earlier instant.
	FireFirst,
	// Use the later instant.
	FireLast
}

public abstract class ScheduleSpec
{
}

public sealed class OnceSpec : ScheduleSpec
{
	public required long AtMs { get; init; }
}

public sealed class IntervalSpec : ScheduleSpec
{
	public required long PeriodMs { get; init; }

	// Grid anchor for FixedRate. Null means the unix epoch.
	public long? AnchorMs { get; init; }

	// Applied by the scheduler at enqueue time, not by the evaluator. The
	// evaluator stays deterministic so fixtures can pin its output.
	public long JitterMs { get; init; }

	public IntervalMode Mode { get; init; } = IntervalMode.FixedDelay;
}

public sealed class CalendarSpec : ScheduleSpec
{
	public required string TimeZoneId { get; init; }
	public required TimeZoneInfo TimeZone { get; init; }

	public required FieldSet Second { get; init; }
	public required FieldSet Minute { get; init; }
	public required FieldSet Hour { get; init; }
	public required FieldSet Day { get; init; }

	// day=last matches the final day of each month, whatever its length.
	public required bool DayLast { get; init; }

	public required FieldSet Dow { get; init; }

	// Restricts Dow to the nth occurrence in the month. 1 to 5, or -1 for last.
	public int? Nth { get; init; }

	public required FieldSet Month { get; init; }

	public DstGapPolicy DstGap { get; init; } = DstGapPolicy.FireAtGapEnd;
	public DstFoldPolicy DstFold { get; init; } = DstFoldPolicy.FireFirst;
}

public static class FieldRanges
{
	public const int SecondMin = 0, SecondMax = 59;
	public const int MinuteMin = 0, MinuteMax = 59;
	public const int HourMin = 0, HourMax = 23;
	public const int DayMin = 1, DayMax = 31;
	public const int DowMin = 0, DowMax = 6;
	public const int MonthMin = 1, MonthMax = 12;

	// Sunday is 0 to match DayOfWeek.Sunday and Date.getUTCDay.
	public static readonly IReadOnlyDictionary<string, int> DowNames = new Dictionary<string, int>
	{
		["sun"] = 0, ["mon"] = 1, ["tue"] = 2, ["wed"] = 3,
		["thu"] = 4, ["fri"] = 5, ["sat"] = 6
	};

	public static readonly IReadOnlyDictionary<string, int> MonthNames = new Dictionary<string, int>
	{
		["jan"] = 1, ["feb"] = 2, ["mar"] = 3, ["apr"] = 4,
		["may"] = 5, ["jun"] = 6, ["jul"] = 7, ["aug"] = 8,
		["sep"] = 9, ["oct"] = 10, ["nov"] = 11, ["dec"] = 12
	};
}
