// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Text.Json;
using Tide.Asgard.Scheduler.Expression;

namespace Tide.Asgard.Scheduler.Execution;

// Default schedule store. Schedules live only as long as the process, which is
// fine when they are declared in code and re-registered at startup. Swap in
// PostgresScheduleStore to have a pause survive a restart, or to add a schedule
// without a deploy.
public sealed class InMemoryScheduleStore : IScheduleStore
{
	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly Lock _gate = new();
	private readonly Dictionary<string, ScheduleRecord> _schedules = [];

	public Task<ScheduleRecord> UpsertAsync(
		ScheduleUpsert input, long nowMs, CancellationToken ct = default)
	{
		lock (_gate)
		{
			_schedules.TryGetValue(input.Name, out var existing);

			// Comparing the canonical spec rather than the expression text, so a
			// reworded expression that means the same thing does not disturb the
			// schedule's position in time.
			var specChanged = existing is null
				|| ScheduleSpecJson.ToJson(existing.Spec) != ScheduleSpecJson.ToJson(input.Spec);

			var record = new ScheduleRecord
			{
				Name = input.Name,
				Handler = input.Handler,
				Payload = input.Payload is null
					? null
					: JsonSerializer.SerializeToNode(input.Payload, JsonOptions),
				Expr = input.Expr,
				Spec = input.Spec,
				Enabled = existing?.Enabled ?? true,
				Misfire = input.Misfire,
				MaxAttempts = input.MaxAttempts,
				NextFireAtMs = specChanged ? input.NextFireAtMs : existing!.NextFireAtMs,
				LastFireAtMs = existing?.LastFireAtMs,
				UpdatedAtMs = nowMs
			};

			_schedules[input.Name] = record;
			return Task.FromResult(record);
		}
	}

	public Task<IReadOnlyList<ScheduleRecord>> ListDueAsync(
		long nowMs, int limit, CancellationToken ct = default)
	{
		lock (_gate)
		{
			return Task.FromResult<IReadOnlyList<ScheduleRecord>>(_schedules.Values
				.Where(s => s.Enabled && s.NextFireAtMs is { } next && next <= nowMs)
				.OrderBy(s => s.NextFireAtMs)
				.ThenBy(s => s.Name, StringComparer.Ordinal)
				.Take(Math.Max(0, limit))
				.ToList());
		}
	}

	public Task<IReadOnlyList<ScheduleRecord>> ListAsync(CancellationToken ct = default)
	{
		lock (_gate)
		{
			return Task.FromResult<IReadOnlyList<ScheduleRecord>>(
				_schedules.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());
		}
	}

	public Task<ScheduleRecord?> GetAsync(string name, CancellationToken ct = default)
	{
		lock (_gate) return Task.FromResult(_schedules.GetValueOrDefault(name));
	}

	public Task AdvanceAsync(
		string name, long? nextFireAtMs, long lastFireAtMs, long nowMs, CancellationToken ct = default)
	{
		lock (_gate)
		{
			if (_schedules.TryGetValue(name, out var existing))
			{
				_schedules[name] = existing with
				{
					NextFireAtMs = nextFireAtMs,
					LastFireAtMs = lastFireAtMs,
					UpdatedAtMs = nowMs
				};
			}
			return Task.CompletedTask;
		}
	}

	public Task<bool> SetEnabledAsync(
		string name, bool enabled, long nowMs, CancellationToken ct = default)
	{
		lock (_gate)
		{
			if (!_schedules.TryGetValue(name, out var existing)) return Task.FromResult(false);

			_schedules[name] = existing with { Enabled = enabled, UpdatedAtMs = nowMs };
			return Task.FromResult(true);
		}
	}

	public Task<bool> RemoveAsync(string name, CancellationToken ct = default)
	{
		lock (_gate) return Task.FromResult(_schedules.Remove(name));
	}
}
