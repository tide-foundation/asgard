// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Tide.Asgard.Scheduler.Execution;

namespace Tide.Asgard.Scheduler.AspNetCore;

// Collects everything the worker needs while the container is still being built.
// Stores, notifiers and observers are given as factories because they usually
// want something from the container themselves, and jobs are built lazily for the
// same reason: a job's handler needs the provider in order to open a scope.
public sealed class SchedulerBuilder
{
	private readonly List<Func<IServiceProvider, JobDefinition>> _jobs = [];
	private readonly List<PendingSchedule> _schedules = [];

	internal SchedulerBuilder(IServiceCollection services) => Services = services;

	public IServiceCollection Services { get; }

	public int Concurrency { get; set; } = 4;
	public long PollIntervalMs { get; set; } = 1_000;
	public long LeaseMs { get; set; } = 30_000;
	public long? HeartbeatMs { get; set; }
	public bool ClaimOnlyRegisteredHandlers { get; set; } = true;
	public RetentionPolicy? Retention { get; set; }
	public RetryPolicy Retry { get; set; } = RetryPolicy.Default;
	public string? Owner { get; set; }

	internal Func<IServiceProvider, IJobStore> StoreFactory { get; private set; }
		= _ => new InMemoryJobStore();

	internal Func<IServiceProvider, IScheduleStore>? ScheduleStoreFactory { get; private set; }
	internal Func<IServiceProvider, IJobNotifier>? NotifierFactory { get; private set; }
	internal Func<IServiceProvider, IJobObserver>? ObserverFactory { get; private set; }

	// Where runs are stored. Defaults to memory, which is only right for local
	// timers: nothing survives a restart.
	public SchedulerBuilder UseStore(Func<IServiceProvider, IJobStore> factory)
	{
		StoreFactory = factory;
		return this;
	}

	// Where schedules are stored. Leave unset to keep them in memory, which is
	// right when they are declared in code.
	public SchedulerBuilder UseScheduleStore(Func<IServiceProvider, IScheduleStore> factory)
	{
		ScheduleStoreFactory = factory;
		return this;
	}

	public SchedulerBuilder UseNotifier(Func<IServiceProvider, IJobNotifier> factory)
	{
		NotifierFactory = factory;
		return this;
	}

	public SchedulerBuilder UseObserver(Func<IServiceProvider, IJobObserver> factory)
	{
		ObserverFactory = factory;
		return this;
	}

	// Logs a line per run through the host's ILogger.
	public SchedulerBuilder UseLogging()
		=> UseObserver(sp => ActivatorUtilities.CreateInstance<LoggingJobObserver>(sp));

	// Registers a job whose handler comes from the container. THandler is added
	// as scoped and resolved from a new scope for each run, so it can depend on
	// scoped services the way a request handler would.
	public SchedulerBuilder AddJob<THandler, TPayload>(string name, int? maxAttempts = null)
		where THandler : class, IJobHandler<TPayload>
	{
		Services.AddScoped<THandler>();

		_jobs.Add(provider => new JobDefinition<TPayload>(
			name,
			async (payload, context) =>
			{
				// A scope per run, disposed when it finishes, so a scoped
				// DbContext behaves the way it does in a request.
				await using var scope = provider.CreateAsyncScope();
				var handler = scope.ServiceProvider.GetRequiredService<THandler>();
				await handler.HandleAsync(payload, context);
			},
			maxAttempts: maxAttempts));

		return this;
	}

	// Registers a job defined inline, for work with no dependencies.
	public SchedulerBuilder AddJob(JobDefinition definition)
	{
		_jobs.Add(_ => definition);
		return this;
	}

	// Registers a recurring schedule. The handler is named rather than passed,
	// because the definition does not exist until the container is built. The
	// name is checked at startup, not silently ignored.
	public SchedulerBuilder AddSchedule(
		string name,
		string expr,
		string handler,
		object? payload = null,
		int? maxAttempts = null,
		MisfirePolicy misfire = MisfirePolicy.FireOnce)
	{
		_schedules.Add(new PendingSchedule(name, expr, handler, payload, maxAttempts, misfire));
		return this;
	}

	internal WorkerOptions BuildOptions(IServiceProvider provider) => new()
	{
		Store = StoreFactory(provider),
		ScheduleStore = ScheduleStoreFactory?.Invoke(provider),
		Notifier = NotifierFactory?.Invoke(provider),
		Observer = ObserverFactory?.Invoke(provider),
		Jobs = _jobs.Select(build => build(provider)).ToList(),
		Owner = Owner,
		Concurrency = Concurrency,
		PollIntervalMs = PollIntervalMs,
		LeaseMs = LeaseMs,
		HeartbeatMs = HeartbeatMs,
		ClaimOnlyRegisteredHandlers = ClaimOnlyRegisteredHandlers,
		Retention = Retention,
		Retry = Retry
	};

	internal IReadOnlyList<ScheduleDefinition> BuildSchedules(IReadOnlyList<JobDefinition> jobs)
	{
		var byName = jobs.ToDictionary(j => j.Name);

		return _schedules.Select(pending =>
		{
			if (!byName.TryGetValue(pending.Handler, out var job))
			{
				throw new InvalidOperationException(
					$"schedule '{pending.Name}' refers to job '{pending.Handler}', which is not registered");
			}

			return new ScheduleDefinition
			{
				Name = pending.Name,
				Expr = pending.Expr,
				Job = job,
				Payload = pending.Payload,
				MaxAttempts = pending.MaxAttempts,
				Misfire = pending.Misfire
			};
		}).ToList();
	}

	private sealed record PendingSchedule(
		string Name, string Expr, string Handler, object? Payload, int? MaxAttempts, MisfirePolicy Misfire);
}
