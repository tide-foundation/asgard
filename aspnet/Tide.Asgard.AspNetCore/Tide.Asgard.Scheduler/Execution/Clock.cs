// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Execution;

// Time is injected rather than read directly so that backoff, lease expiry and
// the worker loop can be tested without waiting. Nothing in the scheduler reads
// the clock outside this file.
public interface IClock
{
	// Wall clock epoch milliseconds. Stored instants and lease deadlines are
	// compared across processes, so they have to be wall clock rather than
	// monotonic.
	long NowMs { get; }

	// Waits roughly ms, returning early if cancelled. Callers must re-read NowMs
	// afterwards rather than assuming the wait landed exactly, because the
	// process may have been suspended or the clock stepped.
	Task Delay(long ms, CancellationToken ct = default);
}

public sealed class SystemClock : IClock
{
	public static readonly SystemClock Instance = new();

	public long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

	public async Task Delay(long ms, CancellationToken ct = default)
	{
		if (ms <= 0) return;
		try
		{
			// Task.Delay is driven by a monotonic source, so a wall clock step
			// cannot stretch or collapse the wait.
			await Task.Delay(TimeSpan.FromMilliseconds(ms), ct);
		}
		catch (OperationCanceledException)
		{
			// Cancellation is a normal shutdown path, not a failure.
		}
	}
}

// Virtual time for tests. Delaying advances the clock and returns immediately,
// so a worker loop runs to completion instantly and deterministically.
public sealed class FakeClock(long startMs) : IClock
{
	public long NowMs { get; private set; } = startMs;

	public void Advance(long ms) => NowMs += ms;

	public Task Delay(long ms, CancellationToken ct = default)
	{
		if (ms > 0) NowMs += ms;
		return Task.CompletedTask;
	}
}
