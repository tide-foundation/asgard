// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tide.Asgard.Scheduler.Execution;

// One object that carries a job's name, its payload type and its handler, so
// registering it and enqueueing it cannot disagree. Pass the definition to both
// and the compiler checks the payload for you:
//
//   var reconcile = Job.Define<ReconcilePayload>(
//       "reconcile-orks", (payload, ctx) => Reconcile(payload.RealmId));
//
//   await worker.EnqueueAsync(reconcile, new ReconcilePayload("tide"));  // checked
//
// The non generic base is what the registry stores, since a dictionary cannot
// hold a different generic type per entry. The payload type is recovered by the
// definition itself, which is the only thing that knows it.
public abstract class JobDefinition
{
	public abstract string Name { get; }
	public abstract int? MaxAttempts { get; }

	// Split in two on purpose. Converting a stored payload and running a handler
	// fail for different reasons and deserve different treatment, and the worker
	// cannot tell them apart if they happen inside one call.
	public abstract object? ConvertPayload(object? payload);

	public abstract Task InvokeAsync(object? convertedPayload, JobContext ctx);
}

public sealed class JobDefinition<TPayload> : JobDefinition
{
	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

	private readonly Func<TPayload, JobContext, Task> _handler;
	private readonly Func<object?, TPayload>? _parse;

	public override string Name { get; }
	public override int? MaxAttempts { get; }

	public JobDefinition(
		string name,
		Func<TPayload, JobContext, Task> handler,
		Func<object?, TPayload>? parse = null,
		int? maxAttempts = null)
	{
		Name = name;
		_handler = handler;
		_parse = parse;
		MaxAttempts = maxAttempts;
	}

	public override object? ConvertPayload(object? payload) => Convert(payload);

	public override Task InvokeAsync(object? convertedPayload, JobContext ctx)
		=> _handler(convertedPayload is TPayload typed ? typed : default!, ctx);

	// Runs on dequeue rather than on enqueue. That is the useful side, because it
	// catches a payload written by an older deploy reaching a handler that now
	// expects a different shape.
	public TPayload Convert(object? payload)
	{
		if (_parse is not null) return _parse(payload);
		if (payload is null) return default!;
		if (payload is TPayload already) return already;

		// Stores hand back JSON, so anything else has to go through it.
		var node = payload as JsonNode ?? JsonSerializer.SerializeToNode(payload, Json);
		return node is null ? default! : node.Deserialize<TPayload>(Json)!;
	}
}

public static class Job
{
	public static JobDefinition<TPayload> Define<TPayload>(
		string name,
		Func<TPayload, JobContext, Task> handler,
		int? maxAttempts = null,
		Func<object?, TPayload>? parse = null)
		=> new(name, handler, parse, maxAttempts);

	// Convenience for handlers that do not need to await anything.
	public static JobDefinition<TPayload> Define<TPayload>(
		string name,
		Action<TPayload, JobContext> handler,
		int? maxAttempts = null,
		Func<object?, TPayload>? parse = null)
		=> new(name, (payload, ctx) =>
		{
			handler(payload, ctx);
			return Task.CompletedTask;
		}, parse, maxAttempts);

	// Convenience for jobs that carry no payload.
	//
	// Both shapes exist so that an async lambda binds to the Task returning one.
	// With only the Action overload an async lambda would compile as async void,
	// and the worker would settle the run the moment the handler yielded rather
	// than when its work finished.
	public static JobDefinition<object?> Define(
		string name, Func<JobContext, Task> handler, int? maxAttempts = null)
		=> new(name, (_, ctx) => handler(ctx), null, maxAttempts);

	public static JobDefinition<object?> Define(
		string name, Action<JobContext> handler, int? maxAttempts = null)
		=> new(name, (_, ctx) =>
		{
			handler(ctx);
			return Task.CompletedTask;
		}, null, maxAttempts);
}

// Raised when a stored payload does not match what the handler expects.
public sealed class PayloadException(string jobName, Exception cause)
	: Exception($"payload for '{jobName}' is not valid: {cause.Message}", cause);
