// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Microsoft.Extensions.Hosting;
using Tide.Asgard.Scheduler.Execution;

namespace Tide.Asgard.Scheduler.AspNetCore;

// Runs the worker for the lifetime of the host.
//
// Applying the schema and registering schedules happen here rather than in the
// container, because both do I/O and a service factory cannot await. Doing them
// in StartAsync also means a database that is unreachable fails startup loudly
// instead of leaving a worker running against nothing.
internal sealed class SchedulerHostedService(
	SchedulerRuntime runtime,
	SchedulerBuilder builder) : IHostedService
{
	private Worker Worker => runtime.Worker;

	public async Task StartAsync(CancellationToken ct)
	{
		if (runtime.Store is ISchemaAwareJobStore store)
		{
			await store.EnsureSchemaAsync(ct);
		}
		if (runtime.ScheduleStore is ISchemaAwareScheduleStore schedules)
		{
			await schedules.EnsureSchemaAsync(ct);
		}

		foreach (var schedule in builder.BuildSchedules(Worker.Jobs))
		{
			await Worker.AddScheduleAsync(schedule, ct);
		}

		Worker.Start();
	}

	// Stops claiming and waits for in flight handlers, so a rolling deploy drains
	// rather than abandoning work to the reaper.
	public Task StopAsync(CancellationToken ct) => Worker.StopAsync();
}
