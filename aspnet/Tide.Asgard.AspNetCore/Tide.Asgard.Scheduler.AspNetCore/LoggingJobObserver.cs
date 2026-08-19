// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Microsoft.Extensions.Logging;
using Tide.Asgard.Scheduler.Execution;

namespace Tide.Asgard.Scheduler.AspNetCore;

// A line per run through the host's logging. Overrides only what it needs, the
// rest keep their no-op defaults.
public sealed class LoggingJobObserver(ILogger<LoggingJobObserver> logger) : IJobObserver
{
	public void RunFinished(RunFinishedEvent e)
	{
		switch (e.Outcome)
		{
			case RunOutcome.Succeeded:
				logger.LogInformation(
					"Job {Job} run {RunId} succeeded in {DurationMs}ms",
					e.Run.Handler, e.Run.Id, e.DurationMs);
				break;

			case RunOutcome.Retried:
				logger.LogWarning(
					e.Error,
					"Job {Job} run {RunId} failed on attempt {Attempt} of {MaxAttempts}, retrying at {NextAttemptAtMs}",
					e.Run.Handler, e.Run.Id, e.Run.Attempt, e.Run.MaxAttempts, e.NextAttemptAtMs);
				break;

			default:
				logger.LogError(
					e.Error,
					"Job {Job} run {RunId} gave up after {Attempt} attempt(s)",
					e.Run.Handler, e.Run.Id, e.Run.Attempt);
				break;
		}
	}
}
