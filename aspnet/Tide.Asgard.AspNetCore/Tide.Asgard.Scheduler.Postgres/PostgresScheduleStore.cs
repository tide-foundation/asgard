// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Tide.Asgard.Scheduler.Execution;
using Tide.Asgard.Scheduler.Expression;

namespace Tide.Asgard.Scheduler.Postgres;

// Durable schedules. Uses the same data source as PostgresJobStore, and the same
// schema creates both tables, so pointing either one at a connection is enough.
public sealed class PostgresScheduleStore : IScheduleStore, ISchemaAwareScheduleStore, IAsyncDisposable
{
	private const string Columns = """
		name, handler, payload, expr, spec, enabled, misfire, max_attempts,
		next_fire_at_ms, last_fire_at_ms, updated_at_ms
		""";

	private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

	private readonly NpgsqlDataSource _dataSource;
	private readonly bool _ownsDataSource;

	public PostgresScheduleStore(NpgsqlDataSource dataSource)
	{
		_dataSource = dataSource;
		_ownsDataSource = false;
	}

	private PostgresScheduleStore(NpgsqlDataSource dataSource, bool ownsDataSource)
	{
		_dataSource = dataSource;
		_ownsDataSource = ownsDataSource;
	}

	public static PostgresScheduleStore Create(string connectionString)
		=> new(NpgsqlDataSource.Create(connectionString), ownsDataSource: true);

	// Both tables come from the same migrations, so this is the job store's
	// schema. Having it here means Worker.CreateAsync can bring a schedule store
	// up on its own.
	public async Task EnsureSchemaAsync(CancellationToken ct = default)
		=> await SchedulerMigrations.MigrateAsync(_dataSource, ct);

	// One statement. On conflict the definition is updated but enabled is left
	// alone, so a redeploy cannot silently resume something an operator paused,
	// and next_fire_at_ms is only reset when the spec actually changed, so a
	// redeploy does not skip or repeat an occurrence either.
	//
	// Every SET expression sees the pre-update row, so comparing against
	// asgard_schedules.spec here is comparing against the stored spec.
	public async Task<ScheduleRecord> UpsertAsync(
		ScheduleUpsert input, long nowMs, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand($"""
			insert into asgard_schedules
			    (name, handler, payload, expr, spec, misfire, max_attempts,
			     next_fire_at_ms, created_at_ms, updated_at_ms)
			values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $9)
			on conflict (name) do update set
			    handler = excluded.handler,
			    payload = excluded.payload,
			    expr = excluded.expr,
			    next_fire_at_ms = case
			        when asgard_schedules.spec is distinct from excluded.spec
			        then excluded.next_fire_at_ms
			        else asgard_schedules.next_fire_at_ms end,
			    spec = excluded.spec,
			    misfire = excluded.misfire,
			    max_attempts = excluded.max_attempts,
			    updated_at_ms = excluded.updated_at_ms
			returning {Columns}
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = input.Name });
		command.Parameters.Add(new NpgsqlParameter { Value = input.Handler });
		command.Parameters.Add(Json(input.Payload));
		command.Parameters.Add(new NpgsqlParameter { Value = input.Expr });
		command.Parameters.Add(new NpgsqlParameter
		{
			Value = ScheduleSpecJson.ToJson(input.Spec),
			NpgsqlDbType = NpgsqlDbType.Jsonb
		});
		command.Parameters.Add(new NpgsqlParameter { Value = MisfireToString(input.Misfire) });
		command.Parameters.Add(Nullable(input.MaxAttempts));
		command.Parameters.Add(Nullable(input.NextFireAtMs));
		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });

		await using var reader = await command.ExecuteReaderAsync(ct);
		await reader.ReadAsync(ct);
		return Read(reader);
	}

	public async Task<IReadOnlyList<ScheduleRecord>> ListDueAsync(
		long nowMs, int limit, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand($"""
			select {Columns} from asgard_schedules
			where enabled and next_fire_at_ms is not null and next_fire_at_ms <= $1
			order by next_fire_at_ms, name
			limit $2
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
		command.Parameters.Add(new NpgsqlParameter { Value = Math.Max(0, limit) });

		return await ReadAllAsync(command, ct);
	}

	public async Task<IReadOnlyList<ScheduleRecord>> ListAsync(CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand(
			$"select {Columns} from asgard_schedules order by name");

		return await ReadAllAsync(command, ct);
	}

	public async Task<ScheduleRecord?> GetAsync(string name, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand(
			$"select {Columns} from asgard_schedules where name = $1");
		command.Parameters.Add(new NpgsqlParameter { Value = name });

		await using var reader = await command.ExecuteReaderAsync(ct);
		return await reader.ReadAsync(ct) ? Read(reader) : null;
	}

	public async Task AdvanceAsync(
		string name, long? nextFireAtMs, long lastFireAtMs, long nowMs, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand("""
			update asgard_schedules
			set next_fire_at_ms = $2, last_fire_at_ms = $3, updated_at_ms = $4
			where name = $1
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = name });
		command.Parameters.Add(Nullable(nextFireAtMs));
		command.Parameters.Add(new NpgsqlParameter { Value = lastFireAtMs });
		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });

		await command.ExecuteNonQueryAsync(ct);
	}

	public async Task<bool> SetEnabledAsync(
		string name, bool enabled, long nowMs, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand(
			"update asgard_schedules set enabled = $2, updated_at_ms = $3 where name = $1");

		command.Parameters.Add(new NpgsqlParameter { Value = name });
		command.Parameters.Add(new NpgsqlParameter { Value = enabled });
		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });

		return await command.ExecuteNonQueryAsync(ct) > 0;
	}

	public async Task<bool> RemoveAsync(string name, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand(
			"delete from asgard_schedules where name = $1");
		command.Parameters.Add(new NpgsqlParameter { Value = name });

		return await command.ExecuteNonQueryAsync(ct) > 0;
	}

	public async ValueTask DisposeAsync()
	{
		if (_ownsDataSource) await _dataSource.DisposeAsync();
	}

	private static async Task<IReadOnlyList<ScheduleRecord>> ReadAllAsync(
		NpgsqlCommand command, CancellationToken ct)
	{
		var records = new List<ScheduleRecord>();
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct)) records.Add(Read(reader));
		return records;
	}

	private static ScheduleRecord Read(NpgsqlDataReader reader) => new()
	{
		Name = reader.GetString(0),
		Handler = reader.GetString(1),
		Payload = reader.IsDBNull(2) ? null : JsonNode.Parse(reader.GetString(2)),
		Expr = reader.GetString(3),
		Spec = ScheduleSpecJson.FromJson(reader.GetString(4)),
		Enabled = reader.GetBoolean(5),
		Misfire = MisfireFromString(reader.GetString(6)),
		MaxAttempts = reader.IsDBNull(7) ? null : reader.GetInt32(7),
		NextFireAtMs = reader.IsDBNull(8) ? null : reader.GetInt64(8),
		LastFireAtMs = reader.IsDBNull(9) ? null : reader.GetInt64(9),
		UpdatedAtMs = reader.GetInt64(10)
	};

	private static NpgsqlParameter Nullable(long? value)
		=> new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Bigint };

	private static NpgsqlParameter Nullable(int? value)
		=> new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer };

	private static NpgsqlParameter Json(object? payload)
		=> new()
		{
			Value = payload is null ? DBNull.Value : JsonSerializer.Serialize(payload, JsonOptions),
			NpgsqlDbType = NpgsqlDbType.Jsonb
		};

	private static string MisfireToString(MisfirePolicy policy) => policy switch
	{
		MisfirePolicy.FireAll => "fire_all",
		MisfirePolicy.Skip => "skip",
		_ => "fire_once"
	};

	private static MisfirePolicy MisfireFromString(string text) => text switch
	{
		"fire_all" => MisfirePolicy.FireAll,
		"skip" => MisfirePolicy.Skip,
		_ => MisfirePolicy.FireOnce
	};
}
