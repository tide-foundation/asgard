// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Expression;

// Error codes are part of the cross-runtime contract. The TypeScript parser must
// emit the same code for the same input so shared fixtures can assert on bad input.
public static class ScheduleErrorCode
{
	public const string Empty = "E_EMPTY";
	public const string UnknownLeader = "E_UNKNOWN_LEADER";
	public const string UnknownField = "E_UNKNOWN_FIELD";
	public const string DuplicateField = "E_DUPLICATE_FIELD";
	public const string MissingValue = "E_MISSING_VALUE";
	public const string BadValue = "E_BAD_VALUE";
	public const string ValueRange = "E_VALUE_RANGE";
	public const string BadStep = "E_BAD_STEP";
	public const string BadRange = "E_BAD_RANGE";
	public const string DayAmbiguous = "E_DAY_AMBIGUOUS";
	public const string NthWithoutDow = "E_NTH_WITHOUT_DOW";
	public const string UnknownTimeZone = "E_UNKNOWN_TIMEZONE";
	public const string BadDuration = "E_BAD_DURATION";
	public const string BadInstant = "E_BAD_INSTANT";
	public const string Trailing = "E_TRAILING";
}

public sealed class ScheduleParseException : Exception
{
	public string Code { get; }

	// Character offset into the original expression.
	public int Offset { get; }

	public ScheduleParseException(string code, int offset, string detail)
		: base($"{code} at {offset}: {detail}")
	{
		Code = code;
		Offset = offset;
	}
}
