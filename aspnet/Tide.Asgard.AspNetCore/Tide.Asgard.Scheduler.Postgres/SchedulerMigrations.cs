// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Npgsql;

namespace Tide.Asgard.Scheduler.Postgres;

public sealed record Migration(int Version, string Name, string Sql);

public static class SchedulerMigrations
{
	// Arbitrary but fixed. Two processes migrating at once take this lock in turn
	// rather than racing each other's DDL.
	private const long AdvisoryLockKey = 3733565341;

	private const string MigrationsTable = """
		create table if not exists asgard_schema_migrations (
		    version       int    primary key,
		    name          text   not null,
		    applied_at_ms bigint not null
		)
		""";

	// Taken under the same advisory lock as the migrations themselves. Two
	// processes running create table if not exists at the same moment otherwise
	// collide in the system catalog, because the existence check and the create
	// are not atomic on their own.
	private static async Task EnsureMigrationsTableAsync(
		NpgsqlDataSource dataSource, CancellationToken ct)
	{
		await using var connection = await dataSource.OpenConnectionAsync(ct);
		await using var transaction = await connection.BeginTransactionAsync(ct);

		await using (var lockCommand = new NpgsqlCommand(
			$"select pg_advisory_xact_lock({AdvisoryLockKey})", connection, transaction))
		{
			await lockCommand.ExecuteNonQueryAsync(ct);
		}

		await using (var create = new NpgsqlCommand(MigrationsTable, connection, transaction))
		{
			await create.ExecuteNonQueryAsync(ct);
		}

		await transaction.CommitAsync(ct);
	}

	// Kept identical to sql/migrations/*.sql, which a test asserts. Migrations are
	// append only: never edit one that has shipped, add the next number instead.
	// Each is written to be safe to apply twice, because a crash between applying
	// one and recording it must not leave the schema unrepeatable.
	public static readonly IReadOnlyList<Migration> All =
	[
		new(1, "001-job-runs", @"-- Durable job runs.
--
-- Times are epoch milliseconds rather than timestamptz. The scheduler already
-- works in epoch milliseconds end to end, and a bigint cannot pick up a session
-- timezone on the way in or out.

create table if not exists asgard_job_runs (
    id                  bigserial primary key,
    schedule_id         text,
    handler             text   not null,
    payload             jsonb,

    -- Two workers materializing the same occurrence produce the same key, and
    -- the unique index means exactly one insert survives. This is what removes
    -- the need for leader election.
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
        check (status in ('pending', 'leased', 'succeeded', 'dead'))
);

-- The claim query's hot path. Partial so it stays small no matter how much
-- settled history accumulates.
create index if not exists asgard_job_runs_due_idx
    on asgard_job_runs (run_at_ms, id)
    where status = 'pending';

-- The reaper's path.
create index if not exists asgard_job_runs_lease_idx
    on asgard_job_runs (lease_expires_at_ms)
    where status = 'leased';"),
		new(2, "002-cancelled-status", @"-- Adds the cancelled status, which an operator can put a run into.
--
-- Guarded on the current definition rather than dropped and recreated blindly,
-- so this is a no-op on a database that already has it. Every migration is
-- written to be safe to apply twice, because a crash between applying one and
-- recording it must not leave the schema unrepeatable.

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
end $$;"),
		new(3, "003-schedules", @"-- Recurring schedules. Optional: a host whose schedules are declared in code can
-- leave these in memory. Putting them here is what lets an operator pause one,
-- or add one, without a deploy.

create table if not exists asgard_schedules (
    name            text primary key,
    handler         text    not null,
    payload         jsonb,

    -- The expression is kept for display. The spec is what actually runs, and is
    -- never re-parsed, so a later change to the language cannot reinterpret a
    -- schedule that is already running.
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
    where enabled and next_fire_at_ms is not null;")
	];

	// Applies whatever is missing, in order, and returns the versions this call
	// actually applied. Safe to call on every startup and from several processes at
	// once: the advisory lock serializes them, and a caller that loses the race is
	// told it applied nothing rather than claiming another process's work.
	//
	// Unlike the TypeScript runner, which only has a query method to work with,
	// this can open a connection and hold a real transaction, so it does.
	public static async Task<IReadOnlyList<int>> MigrateAsync(
		NpgsqlDataSource dataSource, CancellationToken ct = default)
	{
		await EnsureMigrationsTableAsync(dataSource, ct);

		var applied = (await AppliedAsync(dataSource, ct)).ToHashSet();
		var ran = new List<int>();

		foreach (var migration in All)
		{
			// A cheap pre-filter only. The authoritative check is the insert
			// below, which is what makes a concurrent caller honest about what it did.
			if (applied.Contains(migration.Version)) continue;

			await using var connection = await dataSource.OpenConnectionAsync(ct);
			await using var transaction = await connection.BeginTransactionAsync(ct);

			await using (var lockCommand = new NpgsqlCommand(
				$"select pg_advisory_xact_lock({AdvisoryLockKey})", connection, transaction))
			{
				await lockCommand.ExecuteNonQueryAsync(ct);
			}

			await using (var apply = new NpgsqlCommand(migration.Sql, connection, transaction))
			{
				await apply.ExecuteNonQueryAsync(ct);
			}

			await using (var record = new NpgsqlCommand("""
				insert into asgard_schema_migrations (version, name, applied_at_ms)
				values ($1, $2, (extract(epoch from now()) * 1000)::bigint)
				on conflict (version) do nothing
				returning version
				""", connection, transaction))
			{
				record.Parameters.Add(new NpgsqlParameter { Value = migration.Version });
				record.Parameters.Add(new NpgsqlParameter { Value = migration.Name });

				// No row means another process recorded it first.
				if (await record.ExecuteScalarAsync(ct) is not null) ran.Add(migration.Version);
			}

			await transaction.CommitAsync(ct);
		}

		return ran;
	}

	public static async Task<IReadOnlyList<int>> AppliedAsync(
		NpgsqlDataSource dataSource, CancellationToken ct = default)
	{
		await EnsureMigrationsTableAsync(dataSource, ct);

		await using var command = dataSource.CreateCommand(
			"select version from asgard_schema_migrations order by version");

		var versions = new List<int>();
		await using var reader = await command.ExecuteReaderAsync(ct);
		while (await reader.ReadAsync(ct)) versions.Add(reader.GetInt32(0));
		return versions;
	}
}
