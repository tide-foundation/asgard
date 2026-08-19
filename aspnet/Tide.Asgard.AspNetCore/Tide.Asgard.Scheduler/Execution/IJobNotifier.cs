// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Execution;

// Optional. Without one a worker discovers new work by polling, which is fine to
// thousands of jobs a second but means a job enqueued just after a poll waits
// most of an interval before it runs. A notifier lets the worker be woken instead.
//
// Polling always remains the floor. A missed notification, a dropped connection
// or a notifier that throws costs latency, never correctness, which is what makes
// this safe to bolt on.
public interface IJobNotifier
{
	// Wake any worker waiting for work. Called after enqueueing something that is
	// already due.
	Task NotifyAsync(CancellationToken ct = default);

	// Wait up to timeoutMs, returning early when notified or when the token is
	// cancelled. Must not throw: a notifier that cannot reach its backend should
	// fall back to waiting out the timeout.
	Task WaitAsync(long timeoutMs, CancellationToken ct = default);
}

// Process local notifier. Useful with InMemoryJobStore, and the deterministic
// stand-in for a worker's wake-up path in tests.
public sealed class InMemoryNotifier : IJobNotifier
{
	private readonly Lock _gate = new();
	private List<TaskCompletionSource> _waiters = [];

	public Task NotifyAsync(CancellationToken ct = default)
	{
		List<TaskCompletionSource> woken;
		lock (_gate)
		{
			woken = _waiters;
			_waiters = [];
		}

		foreach (var waiter in woken) waiter.TrySetResult();
		return Task.CompletedTask;
	}

	public async Task WaitAsync(long timeoutMs, CancellationToken ct = default)
	{
		if (ct.IsCancellationRequested) return;

		var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		lock (_gate) _waiters.Add(waiter);

		using var registration = ct.Register(() => waiter.TrySetResult());

		var timeout = Task.Delay(TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None);
		await Task.WhenAny(waiter.Task, timeout);

		lock (_gate) _waiters.Remove(waiter);
	}
}
