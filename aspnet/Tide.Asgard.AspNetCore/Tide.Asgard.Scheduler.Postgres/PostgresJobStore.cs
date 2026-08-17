// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Tide.Asgard.Scheduler.Execution;

namespace Tide.Asgard.Scheduler.Postgres;

// Durable job store. The host supplies a connection string, the SDK supplies the
// schema and every query:
//
//   await using var store = PostgresJobStore.Create(connectionString);
//   await store.EnsureSchemaAsync();
//
// Or hand it a data source the application already owns, in which case the
// caller keeps responsibility for disposing it.
public sealed class PostgresJobStore : IJobStore, ISchemaAwareJobStore, IAsyncDisposable
{
	private const string Columns = """
		id, schedule_id, handler, payload, idempotency_key, run_at_ms, status,
		attempt, max_attempts, lease_owner, lease_expires_at_ms, last_error,
		created_at_ms, updated_at_ms
		""";

	private const string InsertColumns = """
		schedule_id, handler, payload, idempotency_key, run_at_ms,
		status, attempt, max_attempts, created_at_ms, updated_at_ms
		""";

	// Kept identical to sql/scheduler-schema.sql, which a test asserts.
	public const string SchemaSql = """
		create table if not exists asgard_job_runs (
		    id                  bigserial primary key,
		    schedule_id         text,
		    handler             text   not null,
		    payload             jsonb,
		    idempotency_key     text   unique,
		    run_at_ms           bigint not null,
		    status              text   not null,
		    attempt             int    not null default 0,
		    max_attempts        int    not null default 1,
		    lease_owner         text,
		    lease_expires_at_ms bigint,
		    last_error          text,
		    created_at_ms       bigint not null,
		    updated_at_ms       bigint not null,

		    constraint asgard_job_runs_status_check
		        check (status in ('pending', 'leased', 'succeeded', 'dead', 'cancelled'))
		);

		create index if not exists asgard_job_runs_due_idx
		    on asgard_job_runs (run_at_ms, id)
		    where status = 'pending';

		create index if not exists asgard_job_runs_lease_idx
		    on asgard_job_runs (lease_expires_at_ms)
		    where status = 'leased';

		do $$
		begin
		    if exists (
		        select 1 from pg_constraint
		        where conname = 'asgard_job_runs_status_check'
		          and pg_get_constraintdef(oid) not like '%cancelled%'
		    ) then
		        alter table asgard_job_runs drop constraint asgard_job_runs_status_check;
		        alter table asgard_job_runs add constraint asgard_job_runs_status_check
		            check (status in ('pending', 'leased', 'succeeded', 'dead', 'cancelled'));
		    end if;
		end $$;

		create table if not exists asgard_schedules (
		    name            text primary key,
		    handler         text    not null,
		    payload         jsonb,
		    expr            text    not null,
		    spec            jsonb   not null,
		    enabled         boolean not null default true,
		    misfire         text    not null default 'fire_once',
		    max_attempts    int,
		    next_fire_at_ms bigint,
		    last_fire_at_ms bigint,
		    created_at_ms   bigint  not null,
		    updated_at_ms   bigint  not null,

		    constraint asgard_schedules_misfire_check
		        check (misfire in ('fire_once', 'fire_all', 'skip'))
		);

		create index if not exists asgard_schedules_due_idx
		    on asgard_schedules (next_fire_at_ms)
		    where enabled and next_fire_at_ms is not null;
		""";

	private readonly NpgsqlDataSource _dataSource;
	private readonly bool _ownsDataSource;

	public PostgresJobStore(NpgsqlDataSource dataSource)
	{
		_dataSource = dataSource;
		_ownsDataSource = false;
	}

	private PostgresJobStore(NpgsqlDataSource dataSource, bool ownsDataSource)
	{
		_dataSource = dataSource;
		_ownsDataSource = ownsDataSource;
	}

	public static PostgresJobStore Create(string connectionString)
		=> new(NpgsqlDataSource.Create(connectionString), ownsDataSource: true);

	// Applies the schema. Safe to call on every startup, and safe to call from
	// several processes at once.
	public async Task EnsureSchemaAsync(CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand(SchemaSql);
		await command.ExecuteNonQueryAsync(ct);
	}

	public async Task<JobRun?> EnqueueAsync(JobRunRequest request, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand($"""
			insert into asgard_job_runs ({InsertColumns})
			values ($1, $2, $3, $4, $5, 'pending', 0, $6, $7, $7)
			on conflict (idempotency_key) do nothing
			returning {Columns}
			""");

		command.Parameters.Add(Nullable(request.ScheduleId));
		command.Parameters.Add(new NpgsqlParameter { Value = request.Handler });
		command.Parameters.Add(Json(request.Payload));
		command.Parameters.Add(Nullable(request.IdempotencyKey));
		command.Parameters.Add(new NpgsqlParameter { Value = request.RunAtMs });
		command.Parameters.Add(new NpgsqlParameter { Value = request.MaxAttempts });
		command.Parameters.Add(new NpgsqlParameter { Value = request.RunAtMs });

		await using var reader = await command.ExecuteReaderAsync(ct);
		return await reader.ReadAsync(ct) ? ReadJobRun(reader) : null;
	}

	// SKIP LOCKED is what lets any number of workers run this concurrently
	// without ever handing the same run to two of them. Rows another worker has
	// locked are stepped over rather than waited on.
	public async Task<IReadOnlyList<JobRun>> ClaimDueAsync(
		string owner, long nowMs, long leaseMs, int max,
		IReadOnlyCollection<string>? handlers = null, CancellationToken ct = default)
	{
		if (max <= 0) return [];
		// An empty allow list means this worker can run nothing, which is not the
		// same as no filter at all.
		if (handlers is { Count: 0 }) return [];

		await using var command = _dataSource.CreateCommand($"""
			update asgard_job_runs
			set status = 'leased',
			    lease_owner = $1,
			    lease_expires_at_ms = $2 + $3,
			    attempt = attempt + 1,
			    updated_at_ms = $2
			where id in (
			    select id from asgard_job_runs
			    where status = 'pending'
			      and run_at_ms <= $2
			      and ($5::text[] is null or handler = any($5))
			    order by run_at_ms, id
			    for update skip locked
			    limit $4
			)
			returning {Columns}
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = owner });
		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
		command.Parameters.Add(new NpgsqlParameter { Value = leaseMs });
		command.Parameters.Add(new NpgsqlParameter { Value = max });
		command.Parameters.Add(new NpgsqlParameter
		{
			Value = handlers is null ? DBNull.Value : handlers.ToArray(),
			NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text
		});

		var claimed = new List<JobRun>();
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct)) claimed.Add(ReadJobRun(reader));
		return claimed;
	}

	public async Task<bool> HeartbeatAsync(
		string runId, long leaseUntilMs, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand("""
			update asgard_job_runs
			set lease_expires_at_ms = $2, updated_at_ms = $2
			where id = $1 and status = 'leased'
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = ParseId(runId) });
		command.Parameters.Add(new NpgsqlParameter { Value = leaseUntilMs });

		return await command.ExecuteNonQueryAsync(ct) > 0;
	}

	public Task CompleteAsync(
		string runId, JobRunRequest? next, long nowMs, CancellationToken ct = default)
		=> SettleAsync(runId, "succeeded", null, next, nowMs, ct);

	public async Task RetryAsync(
		string runId, string error, long runAtMs, long nowMs, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand("""
			update asgard_job_runs
			set status = 'pending',
			    run_at_ms = $2,
			    last_error = $3,
			    lease_owner = null,
			    lease_expires_at_ms = null,
			    updated_at_ms = $4
			where id = $1
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = ParseId(runId) });
		command.Parameters.Add(new NpgsqlParameter { Value = runAtMs });
		command.Parameters.Add(new NpgsqlParameter { Value = error });
		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });

		await command.ExecuteNonQueryAsync(ct);
	}

	public Task DeadLetterAsync(
		string runId, string error, JobRunRequest? next, long nowMs, CancellationToken ct = default)
		=> SettleAsync(runId, "dead", error, next, nowMs, ct);

	public async Task<int> ReapExpiredAsync(long nowMs, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand("""
			update asgard_job_runs
			set status = 'pending',
			    lease_owner = null,
			    lease_expires_at_ms = null,
			    last_error = 'lease expired',
			    updated_at_ms = $1
			where status = 'leased' and lease_expires_at_ms <= $1
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
		return await command.ExecuteNonQueryAsync(ct);
	}

	// Deleting through a bounded subquery rather than one sweeping statement, so
	// a long backlog is cleared in chunks instead of a single delete holding
	// locks across the whole table.
	public async Task<int> PurgeSettledAsync(
		long beforeMs, int limit, bool includeDead = false, CancellationToken ct = default)
	{
		if (limit <= 0) return 0;

		await using var command = _dataSource.CreateCommand("""
			delete from asgard_job_runs
			where id in (
			    select id from asgard_job_runs
			    where status = any($1) and updated_at_ms < $2
			    order by updated_at_ms, id
			    limit $3
			)
			""");

		string[] statuses = includeDead ? ["succeeded", "dead"] : ["succeeded"];
		command.Parameters.Add(new NpgsqlParameter
		{
			Value = statuses,
			NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text
		});
		command.Parameters.Add(new NpgsqlParameter { Value = beforeMs });
		command.Parameters.Add(new NpgsqlParameter { Value = limit });

		return await command.ExecuteNonQueryAsync(ct);
	}

	public async Task<bool> CancelAsync(string runId, long nowMs, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand("""
			update asgard_job_runs
			set status = 'cancelled',
			    lease_owner = null,
			    lease_expires_at_ms = null,
			    last_error = 'cancelled',
			    updated_at_ms = $2
			where id = $1 and status in ('pending', 'leased')
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = ParseId(runId) });
		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });

		return await command.ExecuteNonQueryAsync(ct) > 0;
	}

	public async Task<bool> RequeueAsync(
		string runId, long runAtMs, long nowMs, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand("""
			update asgard_job_runs
			set status = 'pending',
			    run_at_ms = $2,
			    attempt = 0,
			    last_error = null,
			    lease_owner = null,
			    lease_expires_at_ms = null,
			    updated_at_ms = $3
			where id = $1 and status in ('dead', 'cancelled')
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = ParseId(runId) });
		command.Parameters.Add(new NpgsqlParameter { Value = runAtMs });
		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });

		return await command.ExecuteNonQueryAsync(ct) > 0;
	}

	public async Task<JobStoreStats> StatsAsync(long nowMs, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand("""
			select
			    count(*) filter (where status = 'pending')   as pending,
			    count(*) filter (where status = 'leased')    as leased,
			    count(*) filter (where status = 'succeeded') as succeeded,
			    count(*) filter (where status = 'dead')      as dead,
			    count(*) filter (where status = 'cancelled') as cancelled,
			    coalesce(max($1::bigint - run_at_ms)
			        filter (where status = 'pending' and run_at_ms <= $1), 0) as oldest
			from asgard_job_runs
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });

		await using var reader = await command.ExecuteReaderAsync(ct);
		await reader.ReadAsync(ct);

		return new JobStoreStats(
			(int)reader.GetInt64(0),
			(int)reader.GetInt64(1),
			(int)reader.GetInt64(2),
			(int)reader.GetInt64(3),
			(int)reader.GetInt64(4),
			reader.GetInt64(5));
	}

	public async Task<JobRun?> GetAsync(string runId, CancellationToken ct = default)
	{
		await using var command = _dataSource.CreateCommand(
			$"select {Columns} from asgard_job_runs where id = $1");
		command.Parameters.Add(new NpgsqlParameter { Value = ParseId(runId) });

		await using var reader = await command.ExecuteReaderAsync(ct);
		return await reader.ReadAsync(ct) ? ReadJobRun(reader) : null;
	}

	public async ValueTask DisposeAsync()
	{
		if (_ownsDataSource) await _dataSource.DisposeAsync();
	}

	// Settling and chaining are one statement rather than a transaction. A single
	// statement is already atomic, so the successor cannot be lost to a crash in
	// the gap. Selecting from the CTE means the insert only happens if the
	// settling update actually matched a row.
	private async Task SettleAsync(
		string runId, string status, string? error, JobRunRequest? next, long nowMs, CancellationToken ct)
	{
		if (next is null)
		{
			await using var settle = _dataSource.CreateCommand("""
				update asgard_job_runs
				set status = $2,
				    last_error = coalesce($3, last_error),
				    lease_owner = null,
				    lease_expires_at_ms = null,
				    updated_at_ms = $4
				where id = $1
				""");

			settle.Parameters.Add(new NpgsqlParameter { Value = ParseId(runId) });
			settle.Parameters.Add(new NpgsqlParameter { Value = status });
			settle.Parameters.Add(Nullable(error));
			settle.Parameters.Add(new NpgsqlParameter { Value = nowMs });

			await settle.ExecuteNonQueryAsync(ct);
			return;
		}

		await using var command = _dataSource.CreateCommand($"""
			with settled as (
			    update asgard_job_runs
			    set status = $2,
			        last_error = coalesce($3, last_error),
			        lease_owner = null,
			        lease_expires_at_ms = null,
			        updated_at_ms = $4
			    where id = $1
			    returning id
			)
			insert into asgard_job_runs ({InsertColumns})
			select $5, $6, $7, $8, $9, 'pending', 0, $10, $9, $9
			from settled
			on conflict (idempotency_key) do nothing
			""");

		command.Parameters.Add(new NpgsqlParameter { Value = ParseId(runId) });
		command.Parameters.Add(new NpgsqlParameter { Value = status });
		command.Parameters.Add(Nullable(error));
		command.Parameters.Add(new NpgsqlParameter { Value = nowMs });
		command.Parameters.Add(Nullable(next.ScheduleId));
		command.Parameters.Add(new NpgsqlParameter { Value = next.Handler });
		command.Parameters.Add(Json(next.Payload));
		command.Parameters.Add(Nullable(next.IdempotencyKey));
		command.Parameters.Add(new NpgsqlParameter { Value = next.RunAtMs });
		command.Parameters.Add(new NpgsqlParameter { Value = next.MaxAttempts });

		await command.ExecuteNonQueryAsync(ct);
	}

	private static NpgsqlParameter Nullable(string? value)
		=> new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text };

	// Payloads round trip through jsonb, so a handler reading from a durable
	// store receives JSON rather than the object that was enqueued.
	private static NpgsqlParameter Json(object? payload)
		=> new()
		{
			Value = payload is null ? DBNull.Value : JsonSerializer.Serialize(payload),
			NpgsqlDbType = NpgsqlDbType.Jsonb
		};

	private static long ParseId(string runId) => long.Parse(runId);

	private static JobRun ReadJobRun(NpgsqlDataReader reader) => new()
	{
		Id = reader.GetInt64(0).ToString(),
		ScheduleId = reader.IsDBNull(1) ? null : reader.GetString(1),
		Handler = reader.GetString(2),
		Payload = reader.IsDBNull(3) ? null : JsonNode.Parse(reader.GetString(3)),
		IdempotencyKey = reader.IsDBNull(4) ? null : reader.GetString(4),
		RunAtMs = reader.GetInt64(5),
		Status = ParseStatus(reader.GetString(6)),
		Attempt = reader.GetInt32(7),
		MaxAttempts = reader.GetInt32(8),
		LeaseOwner = reader.IsDBNull(9) ? null : reader.GetString(9),
		LeaseExpiresAtMs = reader.IsDBNull(10) ? null : reader.GetInt64(10),
		LastError = reader.IsDBNull(11) ? null : reader.GetString(11),
		CreatedAtMs = reader.GetInt64(12),
		UpdatedAtMs = reader.GetInt64(13)
	};

	private static JobStatus ParseStatus(string status) => status switch
	{
		"pending" => JobStatus.Pending,
		"leased" => JobStatus.Leased,
		"succeeded" => JobStatus.Succeeded,
		"dead" => JobStatus.Dead,
		"cancelled" => JobStatus.Cancelled,
		_ => throw new InvalidOperationException($"unknown job status '{status}'")
	};
}
