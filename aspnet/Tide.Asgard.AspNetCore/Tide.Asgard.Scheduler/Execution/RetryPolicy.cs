// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Execution;

public enum JitterMode
{
	// Exact backoff. Predictable, but a fleet that failed together retries
	// together, which is how a struggling dependency gets knocked over.
	None,
	// Anywhere in [0, delay]. Spreads retries the widest.
	Full,
	// Anywhere in [delay/2, delay]. Keeps most of the backoff while still
	// breaking up the herd.
	Equal
}

public sealed record RetryPolicy
{
	public static readonly RetryPolicy Default = new();

	// Total attempts including the first. 1 means never retry.
	public int MaxAttempts { get; init; } = 5;
	public long BaseMs { get; init; } = 1_000;
	public long CapMs { get; init; } = 300_000;
	public double Multiplier { get; init; } = 2;
	public JitterMode Jitter { get; init; } = JitterMode.Full;

	// A run that has used up attempt attempts may be tried again when this is true.
	public bool ShouldRetry(int attempt) => attempt < MaxAttempts;

	// Delay before the next attempt, where attempt is the one that just failed
	// and counts from 1. The random source is injected so tests can pin the result.
	public long DelayMs(int attempt, Func<double>? random = null)
	{
		var next = random ?? Random.Shared.NextDouble;
		var exponent = Math.Max(0, attempt - 1);
		var raw = BaseMs * Math.Pow(Multiplier, exponent);

		// Cap before jitter, otherwise the cap stops being an upper bound.
		var capped = Math.Min(raw, CapMs);

		return Jitter switch
		{
			JitterMode.None => (long)Math.Round(capped),
			JitterMode.Full => (long)Math.Round(capped * next()),
			JitterMode.Equal => (long)Math.Round(capped / 2 + capped / 2 * next()),
			_ => (long)Math.Round(capped)
		};
	}
}

// Thrown by a handler when the work can never succeed, for example a malformed
// payload. Skips the remaining attempts and sends the run straight to dead.
public sealed class PermanentJobException(string message) : Exception(message);
