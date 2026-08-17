-- Copyright (c) Tide Foundation Limited. All rights reserved.
-- Licensed under the Tide Community Open Code License. See LICENSE in the project root.

-- Canonical schema for the Asgard scheduler's Postgres store.
--
-- Both the TypeScript and .NET stores embed a copy of this file and each test
-- suite asserts its copy still matches, so the two cannot drift apart. Hosts do
-- not need to run this by hand: ensureSchema / EnsureSchemaAsync applies it.
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
        check (status in ('pending', 'leased', 'succeeded', 'dead', 'cancelled'))
);

-- The claim query's hot path. Partial so it stays small no matter how much
-- settled history accumulates.
create index if not exists asgard_job_runs_due_idx
    on asgard_job_runs (run_at_ms, id)
    where status = 'pending';

-- The reaper's path.
create index if not exists asgard_job_runs_lease_idx
    on asgard_job_runs (lease_expires_at_ms)
    where status = 'leased';

-- Schema evolution. Creating tables is idempotent, but altering an existing one
-- is not, so changes that widen a constraint need an explicit fix-up. Guarded on
-- the current definition, so it does no work and takes no lock once applied.
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

-- Recurring schedules. Optional: a host whose schedules are declared in code can
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
    where enabled and next_fire_at_ms is not null;
