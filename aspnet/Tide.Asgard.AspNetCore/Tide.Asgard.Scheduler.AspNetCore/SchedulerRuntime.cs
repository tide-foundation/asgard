// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Tide.Asgard.Scheduler.Execution;

namespace Tide.Asgard.Scheduler.AspNetCore;

// Holds the one store, schedule store and worker the host runs with.
//
// It exists so the factories are invoked exactly once. Calling them again to
// find out whether a store owns its schema would build a second one, and a store
// created from a connection string owns a connection pool, so the second would
// be a pool nobody disposes and nobody uses.
internal sealed class SchedulerRuntime(
	IJobStore store, IScheduleStore? scheduleStore, Worker worker)
{
	public IJobStore Store { get; } = store;
	public IScheduleStore? ScheduleStore { get; } = scheduleStore;
	public Worker Worker { get; } = worker;
}
