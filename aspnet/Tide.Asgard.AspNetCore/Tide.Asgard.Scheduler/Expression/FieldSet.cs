// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Expression;

// A resolved set of allowed values for one calendar field.
// Values are sorted ascending and never empty. Sets are at most 60 entries so
// linear scans are cheap and easier to keep identical across runtimes.
public sealed class FieldSet
{
	private readonly int[] _values;

	public int Min { get; }
	public int Max { get; }
	public IReadOnlyList<int> Values => _values;

	private FieldSet(int min, int max, int[] values)
	{
		Min = min;
		Max = max;
		_values = values;
	}

	public static FieldSet Of(int min, int max, IEnumerable<int> values)
	{
		var sorted = values.Distinct().OrderBy(v => v).ToArray();
		return new FieldSet(min, max, sorted);
	}

	public static FieldSet Any(int min, int max)
	{
		var values = new int[max - min + 1];
		for (var i = 0; i < values.Length; i++) values[i] = min + i;
		return new FieldSet(min, max, values);
	}

	public static FieldSet Single(int min, int max, int value)
		=> new(min, max, [value]);

	// True when every value in range is allowed. Used to detect whether a field
	// actually constrains the search.
	public bool IsAny => _values.Length == Max - Min + 1;

	public bool Has(int value) => Array.IndexOf(_values, value) >= 0;

	// First allowed value greater than or equal to from, or -1 when the search
	// must roll over into the next larger unit.
	public int Next(int from)
	{
		foreach (var v in _values)
		{
			if (v >= from) return v;
		}
		return -1;
	}
}
