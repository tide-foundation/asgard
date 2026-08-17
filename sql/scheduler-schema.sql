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
    where status = 'leased';
