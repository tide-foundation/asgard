// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Expression;

// The only platform specific code in the expression subsystem. Everything else
// is integer arithmetic shared with the TypeScript implementation.
//
// Civil times are carried as DateTime with Kind Unspecified, meaning wall clock
// fields with no zone attached. That is what the evaluator searches over.
public static class TimeZoneShim
{
	private const long MillisPerSecond = 1000;

	// Wall clock in tz at the given instant.
	public static DateTime ToCivil(long instantMs, TimeZoneInfo tz)
	{
		var whole = FloorToSecond(instantMs);
		var utc = DateTimeOffset.FromUnixTimeMilliseconds(whole).UtcDateTime;
		return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, tz), DateTimeKind.Unspecified);
	}

	// Maps a wall clock back to real instants. Returns:
	//   []        the clock never reads this, it falls in a spring forward gap
	//   [t]       the normal case
	//   [t1, t2]  the clock reads this twice, it falls in a fall back overlap
	public static long[] ResolveCivil(DateTime civil, TimeZoneInfo tz)
	{
		civil = DateTime.SpecifyKind(civil, DateTimeKind.Unspecified);

		if (tz.IsInvalidTime(civil)) return [];

		if (tz.IsAmbiguousTime(civil))
		{
			// The larger offset is the earlier instant. Falling back from +11 to
			// +10, 02:30 at +11 happens before 02:30 at +10.
			return tz.GetAmbiguousTimeOffsets(civil)
				.OrderByDescending(o => o)
				.Select(o => new DateTimeOffset(civil, o).ToUnixTimeMilliseconds())
				.ToArray();
		}

		return [new DateTimeOffset(civil, tz.GetUtcOffset(civil)).ToUnixTimeMilliseconds()];
	}

	private static long FloorToSecond(long instantMs)
	{
		var remainder = instantMs % MillisPerSecond;
		if (remainder < 0) remainder += MillisPerSecond;
		return instantMs - remainder;
	}
}
