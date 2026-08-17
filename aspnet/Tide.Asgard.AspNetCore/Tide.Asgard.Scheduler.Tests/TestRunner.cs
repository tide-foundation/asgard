// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Tide.Asgard.Scheduler.Expression;

namespace Tide.Asgard.Scheduler.Tests;

// A test harness small enough to not need a test framework, which keeps the
// scheduler free of third party packages all the way through its tests.
internal sealed class TestRunner
{
	private readonly List<string> _failures = [];
	private int _passed;
	private string _suite = "";

	public void Suite(string name) => _suite = name;

	public void Test(string name, Action body)
	{
		try
		{
			body();
			_passed++;
		}
		catch (Exception e)
		{
			_failures.Add($"{_suite} > {name}\n    {e.Message}");
		}
	}

	// Named separately from Test so that an async lambda cannot be mistaken for
	// a fire and forget Action overload.
	public void TestAsync(string name, Func<Task> body)
	{
		try
		{
			body().GetAwaiter().GetResult();
			_passed++;
		}
		catch (Exception e)
		{
			_failures.Add($"{_suite} > {name}\n    {e.Message}");
		}
	}

	public int Report(string title)
	{
		var total = _passed + _failures.Count;

		if (_failures.Count > 0)
		{
			Console.Error.WriteLine($"FAIL {_failures.Count}/{total} {title}\n");
			foreach (var f in _failures) Console.Error.WriteLine("  " + f + "\n");
			return 1;
		}

		Console.WriteLine($"PASS {_passed}/{total} {title}");
		return 0;
	}
}

internal static class Assert
{
	public static void Equal<T>(T expected, T actual, string? because = null)
	{
		if (EqualityComparer<T>.Default.Equals(expected, actual)) return;
		throw new Exception(
			$"expected {Render(expected)}, got {Render(actual)}{(because is null ? "" : $" ({because})")}");
	}

	public static void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual, string? because = null)
	{
		var e = expected.ToList();
		var a = actual.ToList();
		if (e.SequenceEqual(a)) return;
		throw new Exception(
			$"expected [{string.Join(", ", e.Select(Render))}], " +
			$"got [{string.Join(", ", a.Select(Render))}]{(because is null ? "" : $" ({because})")}");
	}

	public static void True(bool condition, string message)
	{
		if (!condition) throw new Exception(message);
	}

	public static void Contains(string needle, string? haystack)
	{
		if (haystack is not null && haystack.Contains(needle, StringComparison.Ordinal)) return;
		throw new Exception($"expected text containing \"{needle}\", got {Render(haystack)}");
	}

	public static ScheduleParseException Throws(Action body)
	{
		try
		{
			body();
		}
		catch (ScheduleParseException e)
		{
			return e;
		}
		throw new Exception("expected a ScheduleParseException, but nothing was thrown");
	}

	private static string Render<T>(T value) => value switch
	{
		null => "null",
		string s => $"\"{s}\"",
		_ => value.ToString() ?? "null"
	};
}
