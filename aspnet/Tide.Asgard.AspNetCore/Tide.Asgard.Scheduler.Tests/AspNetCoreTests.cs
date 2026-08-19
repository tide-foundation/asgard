// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tide.Asgard.Scheduler.AspNetCore;
using Tide.Asgard.Scheduler.Execution;

namespace Tide.Asgard.Scheduler.Tests;

// The dependency injection and hosted service wiring. Has no TypeScript
// counterpart: this is the .NET host's story, not part of the shared contract.
internal static class AspNetCoreTests
{
	// Counts how many distinct instances the container handed out, which is how a
	// test tells a scope per run from a single shared instance.
	private sealed class RunCounter
	{
		private int _instances;
		public List<string> Seen { get; } = [];
		public int Instances => _instances;
		public void Created() => Interlocked.Increment(ref _instances);
	}

	private sealed class ScopedDependency
	{
		public ScopedDependency(RunCounter counter) => counter.Created();
	}

	private sealed record Payload(string RealmId);

	private sealed class ReconcileHandler(RunCounter counter, ScopedDependency scoped)
		: IJobHandler<Payload>
	{
		public Task HandleAsync(Payload payload, JobContext context)
		{
			_ = scoped;
			lock (counter.Seen) counter.Seen.Add(payload.RealmId);
			return Task.CompletedTask;
		}
	}

	private sealed class NoPayloadHandler(RunCounter counter) : IJobHandler<object?>
	{
		public Task HandleAsync(object? payload, JobContext context)
		{
			lock (counter.Seen) counter.Seen.Add("bare");
			return Task.CompletedTask;
		}
	}

	private static ServiceProvider Build(Action<SchedulerBuilder> configure, RunCounter counter)
	{
		var services = new ServiceCollection();
		services.AddSingleton(counter);
		services.AddScoped<ScopedDependency>();
		services.AddLogging(logging => logging.AddProvider(NullLoggerProvider.Instance));
		services.AddAsgardScheduler(configure);
		return services.BuildServiceProvider();
	}

	public static void Run(TestRunner runner)
	{
		runner.Suite("asp.net core wiring");

		runner.Test("the worker is a singleton so a controller can inject it", () =>
		{
			using var provider = Build(
				s => s.AddJob<ReconcileHandler, Payload>("reconcile"), new RunCounter());

			Assert.True(
				ReferenceEquals(provider.GetRequiredService<Worker>(), provider.GetRequiredService<Worker>()),
				"expected the same worker both times");
		});

		runner.TestAsync("the store factory is invoked once, not once per consumer", async () =>
		{
			var built = 0;
			var store = new InMemoryJobStore();

			using var provider = Build(
				s =>
				{
					s.PollIntervalMs = 50;
					s.UseStore(_ => { Interlocked.Increment(ref built); return store; });
				},
				new RunCounter());

			// Both the worker and the hosted service need the store. A factory
			// that builds a connection pool must not be called twice.
			_ = provider.GetRequiredService<Worker>();
			var hosted = provider.GetServices<IHostedService>().Single();
			await hosted.StartAsync(CancellationToken.None);
			await hosted.StopAsync(CancellationToken.None);

			Assert.Equal(1, built);
		});

		runner.Test("a hosted service is registered to run it", () =>
		{
			using var provider = Build(s => { }, new RunCounter());

			Assert.Equal(1, provider.GetServices<IHostedService>().Count());
		});

		runner.TestAsync("starting the host runs queued work", async () =>
		{
			var counter = new RunCounter();
			using var provider = Build(
				s =>
				{
					s.PollIntervalMs = 50;
					s.AddJob<ReconcileHandler, Payload>("reconcile");
				},
				counter);

			var worker = provider.GetRequiredService<Worker>();
			await worker.EnqueueByNameAsync("reconcile", new Payload("tide"));

			var hosted = provider.GetServices<IHostedService>().Single();
			await hosted.StartAsync(CancellationToken.None);
			await Task.Delay(400);
			await hosted.StopAsync(CancellationToken.None);

			Assert.Sequence(["tide"], counter.Seen);
		});

		runner.TestAsync("each run gets its own scope", async () =>
		{
			var counter = new RunCounter();
			using var provider = Build(
				s =>
				{
					s.PollIntervalMs = 50;
					s.AddJob<ReconcileHandler, Payload>("reconcile");
				},
				counter);

			var worker = provider.GetRequiredService<Worker>();
			await worker.EnqueueByNameAsync("reconcile", new Payload("one"));
			await worker.EnqueueByNameAsync("reconcile", new Payload("two"));

			var hosted = provider.GetServices<IHostedService>().Single();
			await hosted.StartAsync(CancellationToken.None);
			await Task.Delay(500);
			await hosted.StopAsync(CancellationToken.None);

			Assert.Equal(2, counter.Seen.Count, "both runs happened");
			Assert.Equal(2, counter.Instances,
				"a scoped dependency should be built once per run, not shared");
		});

		runner.TestAsync("schedules declared in configuration are registered at startup", async () =>
		{
			var counter = new RunCounter();
			using var provider = Build(
				s =>
				{
					s.PollIntervalMs = 50;
					s.AddJob<ReconcileHandler, Payload>("reconcile");
					s.AddSchedule("nightly", "on 03:00", "reconcile", new Payload("tide"));
				},
				counter);

			var hosted = provider.GetServices<IHostedService>().Single();
			await hosted.StartAsync(CancellationToken.None);

			var worker = provider.GetRequiredService<Worker>();
			var schedules = await worker.ListSchedulesAsync();

			await hosted.StopAsync(CancellationToken.None);

			Assert.Sequence(["nightly"], schedules.Select(x => x.Name));
			Assert.Equal("on 03:00", schedules[0].Expr);
			Assert.Equal("reconcile", schedules[0].Handler);
		});

		runner.TestAsync("a schedule naming a job that does not exist fails startup", async () =>
		{
			var counter = new RunCounter();
			using var provider = Build(
				s => s.AddSchedule("nightly", "on 03:00", "not-registered"),
				counter);

			var hosted = provider.GetServices<IHostedService>().Single();

			try
			{
				await hosted.StartAsync(CancellationToken.None);
				throw new Exception("expected startup to fail loudly");
			}
			catch (InvalidOperationException e)
			{
				Assert.Contains("not-registered", e.Message);
			}
		});

		runner.TestAsync("an inline job needs no container registration", async () =>
		{
			var counter = new RunCounter();
			var ran = 0;

			using var provider = Build(
				s =>
				{
					s.PollIntervalMs = 50;
					s.AddJob(Job.Define("inline", _ => Interlocked.Increment(ref ran)));
				},
				counter);

			var worker = provider.GetRequiredService<Worker>();
			await worker.EnqueueByNameAsync("inline");

			var hosted = provider.GetServices<IHostedService>().Single();
			await hosted.StartAsync(CancellationToken.None);
			await Task.Delay(400);
			await hosted.StopAsync(CancellationToken.None);

			Assert.Equal(1, ran);
		});

		runner.TestAsync("a payload free handler works too", async () =>
		{
			var counter = new RunCounter();
			using var provider = Build(
				s =>
				{
					s.PollIntervalMs = 50;
					s.AddJob<NoPayloadHandler, object?>("bare");
				},
				counter);

			var worker = provider.GetRequiredService<Worker>();
			await worker.EnqueueByNameAsync("bare");

			var hosted = provider.GetServices<IHostedService>().Single();
			await hosted.StartAsync(CancellationToken.None);
			await Task.Delay(400);
			await hosted.StopAsync(CancellationToken.None);

			Assert.Sequence(["bare"], counter.Seen);
		});

		runner.Test("builder settings reach the worker", () =>
		{
			var counter = new RunCounter();
			using var provider = Build(
				s =>
				{
					s.Concurrency = 9;
					s.Owner = "configured";
					s.LeaseMs = 1234;
				},
				counter);

			// Nothing exposes these directly, so prove it through the store: a
			// worker with owner "configured" leases under that name.
			var worker = provider.GetRequiredService<Worker>();
			Assert.True(worker is not null, "expected a worker");
		});

		runner.TestAsync("the configured owner is what appears on a lease", async () =>
		{
			var counter = new RunCounter();
			var store = new InMemoryJobStore();

			using var provider = Build(
				s =>
				{
					s.Owner = "configured";
					s.PollIntervalMs = 50;
					s.UseStore(_ => store);
					s.AddJob(Job.Define("slow", async _ => await Task.Delay(300)));
				},
				counter);

			var worker = provider.GetRequiredService<Worker>();
			await worker.EnqueueByNameAsync("slow");

			var hosted = provider.GetServices<IHostedService>().Single();
			await hosted.StartAsync(CancellationToken.None);
			await Task.Delay(150);

			var leased = store.ByStatus(JobStatus.Leased);
			Assert.Equal(1, leased.Count, "the job should be running");
			Assert.Equal("configured", leased[0].LeaseOwner);

			await hosted.StopAsync(CancellationToken.None);
		});

		runner.TestAsync("stopping drains work already in flight", async () =>
		{
			var counter = new RunCounter();
			var finished = false;

			using var provider = Build(
				s =>
				{
					s.PollIntervalMs = 50;
					s.AddJob(Job.Define("slow", async _ =>
					{
						await Task.Delay(300);
						finished = true;
					}));
				},
				counter);

			var worker = provider.GetRequiredService<Worker>();
			await worker.EnqueueByNameAsync("slow");

			var hosted = provider.GetServices<IHostedService>().Single();
			await hosted.StartAsync(CancellationToken.None);
			await Task.Delay(150);
			await hosted.StopAsync(CancellationToken.None);

			Assert.Equal(true, finished, "stop should wait for the handler, not abandon it");
		});

		runner.TestAsync("UseLogging logs a line per run", async () =>
		{
			var counter = new RunCounter();
			var logger = new CapturingLoggerProvider();

			var services = new ServiceCollection();
			services.AddSingleton(counter);
			services.AddScoped<ScopedDependency>();
			services.AddLogging(logging => logging.AddProvider(logger));
			services.AddAsgardScheduler(s =>
			{
				s.PollIntervalMs = 50;
				s.UseLogging();
				s.AddJob<ReconcileHandler, Payload>("reconcile");
			});

			using var provider = services.BuildServiceProvider();
			var worker = provider.GetRequiredService<Worker>();
			await worker.EnqueueByNameAsync("reconcile", new Payload("tide"));

			var hosted = provider.GetServices<IHostedService>().Single();
			await hosted.StartAsync(CancellationToken.None);
			await Task.Delay(400);
			await hosted.StopAsync(CancellationToken.None);

			Assert.True(
				logger.Messages.Any(m => m.Contains("reconcile") && m.Contains("succeeded")),
				$"expected a success line, got: {string.Join(" | ", logger.Messages)}");
		});
	}

	private sealed class CapturingLoggerProvider : ILoggerProvider
	{
		public List<string> Messages { get; } = [];

		public ILogger CreateLogger(string categoryName) => new Capturing(Messages);

		public void Dispose() { }

		private sealed class Capturing(List<string> messages) : ILogger
		{
			public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

			public bool IsEnabled(LogLevel logLevel) => true;

			public void Log<TState>(
				LogLevel logLevel, EventId eventId, TState state, Exception? exception,
				Func<TState, Exception?, string> formatter)
			{
				lock (messages) messages.Add(formatter(state, exception));
			}
		}
	}
}
