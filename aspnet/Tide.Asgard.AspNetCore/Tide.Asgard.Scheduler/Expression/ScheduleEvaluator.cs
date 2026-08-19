// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Expression;

public static class ScheduleEvaluator
{
	// A calendar spec can legitimately never fire again, for example day=30 with
	// month=2, so the search needs a bound. The Gregorian calendar repeats exactly
	// every 400 years, weekday alignment included, so anything that has not fired
	// within one cycle never will. A shorter horizon would be a guess: day=29
	// month=2 already has an 8 year gap across a century that skips its leap day,
	// and nth=5 in February is rarer still.
	private const int HorizonYears = 400;
	private const int MaxIterations = 200_000;

	// How far past a spring forward gap to look for the moment the clock resumes.
	private const int GapProbeMinutes = 240;

	// First instant strictly after afterMs at which this schedule fires, or null
	// when it never fires again.
	//
	// For IntervalMode.FixedDelay the caller passes the completion instant of the
	// previous run, since that mode measures from completion rather than from a grid.
	// Jitter is deliberately not applied here so this method stays deterministic
	// and testable against shared fixtures.
	public static long? NextFire(ScheduleSpec spec, long afterMs) => spec switch
	{
		OnceSpec once => once.AtMs > afterMs ? once.AtMs : null,
		IntervalSpec interval => NextInterval(interval, afterMs),
		CalendarSpec calendar => NextCalendar(calendar, afterMs),
		_ => throw new ArgumentOutOfRangeException(nameof(spec), $"unsupported spec {spec.GetType().Name}")
	};

	private static long NextInterval(IntervalSpec spec, long afterMs)
	{
		if (spec.Mode == IntervalMode.FixedDelay) return afterMs + spec.PeriodMs;

		var anchor = spec.AnchorMs ?? 0;
		var ticks = FloorDiv(afterMs - anchor, spec.PeriodMs) + 1;
		return anchor + ticks * spec.PeriodMs;
	}

	private static long FloorDiv(long a, long b)
	{
		var q = a / b;
		if (a % b != 0 && (a < 0) != (b < 0)) q--;
		return q;
	}

	private static long? NextCalendar(CalendarSpec spec, long afterMs)
	{
		var p = TimeZoneShim.ToCivil(afterMs, spec.TimeZone);
		var startYear = p.Year;

		for (var i = 0; i < MaxIterations; i++)
		{
			if (p.Year > startYear + HorizonYears || p.Year >= 9999) return null;

			var month = p.Month;
			var nextMonth = spec.Month.Next(month);
			if (nextMonth < 0) { p = new DateTime(p.Year + 1, 1, 1); continue; }
			if (nextMonth != month) { p = new DateTime(p.Year, nextMonth, 1); continue; }

			if (!DayMatches(spec, p)) { p = p.Date.AddDays(1); continue; }

			var hour = p.Hour;
			var nextHour = spec.Hour.Next(hour);
			if (nextHour < 0) { p = p.Date.AddDays(1); continue; }
			if (nextHour != hour) { p = p.Date.AddHours(nextHour); continue; }

			var minute = p.Minute;
			var nextMinute = spec.Minute.Next(minute);
			if (nextMinute < 0) { p = p.Date.AddHours(hour + 1); continue; }
			if (nextMinute != minute) { p = p.Date.AddHours(hour).AddMinutes(nextMinute); continue; }

			var second = p.Second;
			var nextSecond = spec.Second.Next(second);
			if (nextSecond < 0) { p = p.Date.AddHours(hour).AddMinutes(minute + 1); continue; }
			if (nextSecond != second)
			{
				p = p.Date.AddHours(hour).AddMinutes(minute).AddSeconds(nextSecond);
				continue;
			}

			// Every calendar field matches. Map the wall clock onto a real instant.
			var instant = ResolveInstant(spec, p);
			if (instant is not null && instant > afterMs) return instant;

			// Either the clock does not exist here, or the instant we found is not
			// yet past afterMs. Move on by one second and keep searching.
			p = p.AddSeconds(1);
		}

		return null;
	}

	private static long? ResolveInstant(CalendarSpec spec, DateTime civil)
	{
		var candidates = TimeZoneShim.ResolveCivil(civil, spec.TimeZone);

		if (candidates.Length == 1) return candidates[0];
		if (candidates.Length > 1)
		{
			return spec.DstFold == DstFoldPolicy.FireLast ? candidates[^1] : candidates[0];
		}
		if (spec.DstGap == DstGapPolicy.Skip) return null;
		return FindGapEnd(civil, spec.TimeZone);
	}

	// The requested wall clock does not exist. Find the instant the clock resumes,
	// which is the first existing wall clock at or after the requested one.
	// Walk forward in minutes to bracket the gap, then refine to the second.
	private static long? FindGapEnd(DateTime civil, TimeZoneInfo tz)
	{
		for (var m = 1; m <= GapProbeMinutes; m++)
		{
			var coarse = TimeZoneShim.ResolveCivil(civil.AddMinutes(m), tz);
			if (coarse.Length == 0) continue;

			var basis = civil.AddMinutes(m - 1);
			for (var s = 1; s <= 60; s++)
			{
				var refined = TimeZoneShim.ResolveCivil(basis.AddSeconds(s), tz);
				if (refined.Length > 0) return refined[0];
			}
			return coarse[0];
		}
		return null;
	}

	private static bool DayMatches(CalendarSpec spec, DateTime p)
	{
		if (!spec.Dow.Has((int)p.DayOfWeek)) return false;
		if (spec.Nth is not null && !NthMatches(p, spec.Nth.Value)) return false;

		if (spec.DayLast) return p.Day == DateTime.DaysInMonth(p.Year, p.Month);
		return spec.Day.Has(p.Day);
	}

	private static bool NthMatches(DateTime p, int nth)
	{
		if (nth == -1) return p.Day + 7 > DateTime.DaysInMonth(p.Year, p.Month);
		return ((p.Day - 1) / 7) + 1 == nth;
	}
}
