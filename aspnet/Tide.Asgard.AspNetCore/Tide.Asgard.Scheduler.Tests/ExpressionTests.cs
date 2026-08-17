// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Globalization;
using Tide.Asgard.Scheduler.Expression;

namespace Tide.Asgard.Scheduler.Tests;

// Unit tests for behaviour the shared fixtures do not reach, mostly the shape of
// the parsed spec rather than the instants it produces. The TypeScript suite in
// tests/ts/expression.test.js mirrors this file case for case.
internal static class ExpressionTests
{
	public static void Run(TestRunner runner)
	{
		runner.Suite("duration literals");

		runner.Test("single unit", () =>
			Assert.Equal(30_000L, DurationParser.ParseDuration("30s")));

		runner.Test("compound units descend in size", () =>
			Assert.Equal(5_400_000L, DurationParser.ParseDuration("1h30m")));

		runner.Test("milliseconds", () =>
			Assert.Equal(500L, DurationParser.ParseDuration("500ms")));

		runner.Test("units out of order are rejected", () =>
			Assert.Equal(ScheduleErrorCode.BadDuration,
				Assert.Throws(() => DurationParser.ParseDuration("30m1h")).Code));

		runner.Test("zero is rejected", () =>
			Assert.Equal(ScheduleErrorCode.BadDuration,
				Assert.Throws(() => DurationParser.ParseDuration("0s")).Code));

		runner.Suite("defaulting rule");

		runner.Test("naming hour zeroes minute and second and leaves the rest open", () =>
		{
			var spec = Cal("on hour=3");
			Assert.Sequence([0], spec.Second.Values);
			Assert.Sequence([0], spec.Minute.Values);
			Assert.Sequence([3], spec.Hour.Values);
			Assert.True(spec.Day.IsAny, "day should be unrestricted");
			Assert.True(spec.Month.IsAny, "month should be unrestricted");
		});

		runner.Test("naming month collapses day as well", () =>
		{
			var spec = Cal("on month=7");
			Assert.Sequence([7], spec.Month.Values);
			Assert.Sequence([1], spec.Day.Values);
			Assert.Sequence([0], spec.Hour.Values);
		});

		runner.Test("naming second leaves minute and hour open", () =>
		{
			var spec = Cal("on second=30");
			Assert.Sequence([30], spec.Second.Values);
			Assert.True(spec.Minute.IsAny, "minute should be unrestricted");
			Assert.True(spec.Hour.IsAny, "hour should be unrestricted");
		});

		runner.Test("dow counts as the day rung", () =>
		{
			var spec = Cal("on dow=mon");
			Assert.Sequence([1], spec.Dow.Values);
			Assert.True(spec.Day.IsAny, "day should be unrestricted");
			Assert.Sequence([0], spec.Hour.Values);
		});

		runner.Suite("value syntax");

		runner.Test("list", () =>
			Assert.Sequence([1, 5, 9], Cal("on hour=1,5,9").Hour.Values));

		runner.Test("range", () =>
			Assert.Sequence([9, 10, 11, 12], Cal("on hour=9-12").Hour.Values));

		runner.Test("wildcard with step", () =>
			Assert.Sequence([0, 6, 12, 18], Cal("on hour=*/6").Hour.Values));

		runner.Test("range with step", () =>
			Assert.Sequence([9, 13, 17], Cal("on hour=9-17/4").Hour.Values));

		runner.Test("bare value with step runs to the field maximum", () =>
			Assert.Sequence([50, 55], Cal("on minute=50/5").Minute.Values));

		runner.Test("named weekdays resolve with sunday as zero", () =>
			Assert.Sequence([0, 3, 6], Cal("on dow=sun,wed,sat").Dow.Values));

		runner.Test("named months", () =>
			Assert.Sequence([1, 12], Cal("on month=jan,dec").Month.Values));

		runner.Suite("time literals");

		runner.Test("HH:MM expands to hour and minute", () =>
		{
			var spec = Cal("on 09:30");
			Assert.Sequence([9], spec.Hour.Values);
			Assert.Sequence([30], spec.Minute.Values);
			Assert.Sequence([0], spec.Second.Values);
		});

		runner.Test("HH:MM:SS expands to seconds as well", () =>
			Assert.Sequence([15], Cal("on 09:30:15").Second.Values));

		runner.Test("leading zeros are accepted", () =>
			Assert.Sequence([3], Cal("on 03:00").Hour.Values));

		runner.Test("a time literal is equivalent to naming the fields", () =>
		{
			var after = At("2026-08-17T00:00:00Z");
			Assert.Equal(
				ScheduleEvaluator.NextFire(Cal("on hour=9 minute=30"), after),
				ScheduleEvaluator.NextFire(Cal("on 09:30"), after));
		});

		runner.Suite("rejected combinations");

		runner.Test("day and dow both restricted", () =>
			Assert.Equal(ScheduleErrorCode.DayAmbiguous,
				Assert.Throws(() => ScheduleParser.Parse("on day=1 dow=mon")).Code));

		runner.Test("nth without dow", () =>
			Assert.Equal(ScheduleErrorCode.NthWithoutDow,
				Assert.Throws(() => ScheduleParser.Parse("on nth=2 hour=10")).Code));

		runner.Test("errors carry the offending character offset", () =>
		{
			var thrown = Assert.Throws(() => ScheduleParser.Parse("on hour=3 minute=99"));
			Assert.Equal(ScheduleErrorCode.ValueRange, thrown.Code);
			Assert.Equal(17, thrown.Offset);
		});

		runner.Suite("defaults");

		runner.Test("timezone defaults to UTC", () =>
			Assert.Equal("UTC", Cal("on hour=3").TimeZoneId));

		runner.Test("dst policies default to firing at the gap end and the first fold", () =>
		{
			var spec = Cal("on hour=3");
			Assert.Equal(DstGapPolicy.FireAtGapEnd, spec.DstGap);
			Assert.Equal(DstFoldPolicy.FireFirst, spec.DstFold);
		});

		runner.Suite("intervals");

		runner.Test("fixed delay measures from the instant passed in", () =>
		{
			var spec = Interval("every 5m");
			Assert.Equal(IntervalMode.FixedDelay, spec.Mode);
			Assert.Equal(At("2026-08-17T00:07:13Z"),
				ScheduleEvaluator.NextFire(spec, At("2026-08-17T00:02:13Z")));
		});

		runner.Test("an anchor implies fixed rate and snaps to the grid", () =>
		{
			var spec = Interval("every 15m from 2026-01-01T00:00:00Z");
			Assert.Equal(IntervalMode.FixedRate, spec.Mode);
			Assert.Equal(At("2026-08-17T04:15:00Z"),
				ScheduleEvaluator.NextFire(spec, At("2026-08-17T04:07:00Z")));
		});

		runner.Test("jitter is parsed but not applied by the evaluator", () =>
		{
			var spec = Interval("every 1h jitter 30s");
			Assert.Equal(30_000L, spec.JitterMs);
			Assert.Equal(At("2026-08-17T01:00:00Z"),
				ScheduleEvaluator.NextFire(spec, At("2026-08-17T00:00:00Z")));
		});

		runner.Suite("never fires again");

		runner.Test("a one shot in the past", () =>
			Assert.Equal(null, ScheduleEvaluator.NextFire(
				ScheduleParser.Parse("at 2026-01-01T00:00:00Z"), At("2026-08-17T00:00:00Z"))));

		runner.Test("a calendar that can never match", () =>
			Assert.Equal(null, ScheduleEvaluator.NextFire(
				ScheduleParser.Parse("on month=2 day=30"), At("2026-08-17T00:00:00Z"))));

		runner.Suite("specs are reusable");

		runner.Test("one spec drives many evaluations", () =>
		{
			var spec = Cal("on hour=3");
			var cursor = At("2026-08-17T00:00:00Z");
			var fires = new List<string>();

			for (var i = 0; i < 3; i++)
			{
				cursor = ScheduleEvaluator.NextFire(spec, cursor)!.Value;
				fires.Add(DateTimeOffset.FromUnixTimeMilliseconds(cursor)
					.ToString("yyyy-MM-ddTHH:mm:ss'Z'", CultureInfo.InvariantCulture));
			}

			Assert.Sequence(
				["2026-08-17T03:00:00Z", "2026-08-18T03:00:00Z", "2026-08-19T03:00:00Z"], fires);
		});
	}

	private static CalendarSpec Cal(string expr) => (CalendarSpec)ScheduleParser.Parse(expr);

	private static IntervalSpec Interval(string expr) => (IntervalSpec)ScheduleParser.Parse(expr);

	private static long At(string iso) => DateTimeOffset
		.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
		.ToUnixTimeMilliseconds();
}
