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
