// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

namespace Tide.Asgard.Scheduler.Execution;

public enum RunOutcome
{
	Succeeded,
	Retried,
	Dead
}

public sealed record RunStartedEvent(JobRun Run, long AtMs);

public sealed record RunFinishedEvent(
	JobRun Run,
	RunOutcome Outcome,
	long DurationMs,
	// Present when the outcome is not Succeeded.
	Exception? Error = null,
	// When the next attempt is due, for a retry. Null for a terminal outcome.
	long? NextAttemptAtMs = null);

public sealed record TickFinishedEvent(TickResult Result, long DurationMs);

// Somewhere to hang a log line, a metric or a trace span. Every method has a
// no-op default, so implement only what you need.
//
// Callbacks are synchronous on purpose. Awaiting an observer would put the
// caller's logging or tracing on the critical path of every run, and a slow one
// would quietly become a throughput problem. Anything expensive should be queued
// by the observer itself.
//
// A callback that throws is reported through OnError and otherwise ignored.
// Observing work must not be able to break it.
public interface IJobObserver
{
	void RunStarted(RunStartedEvent e) { }

	void RunFinished(RunFinishedEvent e) { }

	void TickFinished(TickFinishedEvent e) { }
}
