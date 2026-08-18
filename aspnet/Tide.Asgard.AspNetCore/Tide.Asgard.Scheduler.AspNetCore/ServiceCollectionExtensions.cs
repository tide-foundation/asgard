// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Tide.Asgard.Scheduler.Execution;

namespace Tide.Asgard.Scheduler.AspNetCore;

public static class ServiceCollectionExtensions
{
	// Registers the scheduler and runs it for the lifetime of the host:
	//
	//   builder.Services.AddAsgardScheduler(scheduler => scheduler
	//       .UseStore(_ => PostgresJobStore.Create(connectionString))
	//       .UseLogging()
	//       .AddJob<ReconcileOrks, ReconcilePayload>("reconcile-orks")
	//       .AddSchedule("nightly", "on 03:00", "reconcile-orks", new ReconcilePayload("tide")));
	//
	// The Worker is registered as a singleton, so a controller or endpoint can
	// inject it to pause a schedule, trigger one, or cancel a run.
	public static IServiceCollection AddAsgardScheduler(
		this IServiceCollection services, Action<SchedulerBuilder> configure)
	{
		var builder = new SchedulerBuilder(services);
		configure(builder);

		services.AddSingleton(builder);

		services.AddSingleton(provider =>
		{
			// Resolved once, here, and shared. See SchedulerRuntime.
			var store = builder.StoreFactory(provider);
			var scheduleStore = builder.ScheduleStoreFactory?.Invoke(provider);

			return new SchedulerRuntime(
				store, scheduleStore, new Worker(builder.BuildOptions(provider, store, scheduleStore)));
		});

		services.AddSingleton(provider => provider.GetRequiredService<SchedulerRuntime>().Worker);
		services.AddHostedService<SchedulerHostedService>();

		return services;
	}
}
