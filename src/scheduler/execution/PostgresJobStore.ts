// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { JobRun, JobRunRequest, JobStatus } from "./JobRun";
import { JobStore } from "./JobStore";

// The host supplies the connection, the SDK supplies the SQL. This is the whole
// surface needed, and node-postgres Pool and Client both satisfy it structurally,
// so nothing here has to import pg and the package keeps no dependencies:
//
//   import { Pool } from "pg";
//   const store = new PostgresJobStore(new Pool({ connectionString }));
//   await store.ensureSchema();
export interface SqlClient {
    query(text: string, values?: unknown[]): Promise<{ rows: any[]; rowCount: number | null }>;
}

// Kept identical to sql/scheduler-schema.sql, which a test asserts.
export const SCHEDULER_SCHEMA_SQL = `create table if not exists asgard_job_runs (
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
        check (status in ('pending', 'leased', 'succeeded', 'dead'))
);

create index if not exists asgard_job_runs_due_idx
    on asgard_job_runs (run_at_ms, id)
    where status = 'pending';

create index if not exists asgard_job_runs_lease_idx
    on asgard_job_runs (lease_expires_at_ms)
    where status = 'leased';`;

const COLUMNS = `id, schedule_id, handler, payload, idempotency_key, run_at_ms, status,
    attempt, max_attempts, lease_owner, lease_expires_at_ms, last_error,
    created_at_ms, updated_at_ms`;

const INSERT_COLUMNS = `schedule_id, handler, payload, idempotency_key, run_at_ms,
    status, attempt, max_attempts, created_at_ms, updated_at_ms`;

export class PostgresJobStore implements JobStore {
    constructor(private readonly sql: SqlClient) { }

    // Applies the schema. Safe to call on every startup, and safe to call from
    // several processes at once.
    async ensureSchema(): Promise<void> {
        await this.sql.query(SCHEDULER_SCHEMA_SQL);
    }

    async enqueue(request: JobRunRequest): Promise<JobRun | null> {
        const result = await this.sql.query(
            `insert into asgard_job_runs (${INSERT_COLUMNS})
             values ($1, $2, $3::jsonb, $4, $5, 'pending', 0, $6, $7, $7)
             on conflict (idempotency_key) do nothing
             returning ${COLUMNS}`,
            [
                request.scheduleId ?? null,
                request.handler,
                serialize(request.payload),
                request.idempotencyKey ?? null,
                request.runAtMs,
                request.maxAttempts ?? 1,
                request.runAtMs
            ]);

        return result.rows.length === 0 ? null : toJobRun(result.rows[0]);
    }

    // SKIP LOCKED is what lets any number of workers run this concurrently
    // without ever handing the same run to two of them. Rows another worker has
    // locked are stepped over rather than waited on.
    async claimDue(owner: string, nowMs: number, leaseMs: number, max: number): Promise<JobRun[]> {
        if (max <= 0) return [];

        const result = await this.sql.query(
            `update asgard_job_runs
             set status = 'leased',
                 lease_owner = $1,
                 lease_expires_at_ms = $2 + $3,
                 attempt = attempt + 1,
                 updated_at_ms = $2
             where id in (
                 select id from asgard_job_runs
                 where status = 'pending' and run_at_ms <= $2
                 order by run_at_ms, id
                 for update skip locked
                 limit $4
             )
             returning ${COLUMNS}`,
            [owner, nowMs, leaseMs, max]);

        return result.rows.map(toJobRun);
    }

    async heartbeat(runId: string, leaseUntilMs: number): Promise<boolean> {
        const result = await this.sql.query(
            `update asgard_job_runs
             set lease_expires_at_ms = $2, updated_at_ms = $2
             where id = $1 and status = 'leased'`,
            [runId, leaseUntilMs]);

        return (result.rowCount ?? 0) > 0;
    }

    async complete(runId: string, next: JobRunRequest | null, nowMs: number): Promise<void> {
        await this.settle(runId, JobStatus.Succeeded, null, next, nowMs);
    }

    async retry(runId: string, error: string, runAtMs: number, nowMs: number): Promise<void> {
        await this.sql.query(
            `update asgard_job_runs
             set status = 'pending',
                 run_at_ms = $2,
                 last_error = $3,
                 lease_owner = null,
                 lease_expires_at_ms = null,
                 updated_at_ms = $4
             where id = $1`,
            [runId, runAtMs, error, nowMs]);
    }

    async deadLetter(
        runId: string, error: string, next: JobRunRequest | null, nowMs: number): Promise<void> {
        await this.settle(runId, JobStatus.Dead, error, next, nowMs);
    }

    async reapExpired(nowMs: number): Promise<number> {
        const result = await this.sql.query(
            `update asgard_job_runs
             set status = 'pending',
                 lease_owner = null,
                 lease_expires_at_ms = null,
                 last_error = 'lease expired',
                 updated_at_ms = $1
             where status = 'leased' and lease_expires_at_ms <= $1`,
            [nowMs]);

        return result.rowCount ?? 0;
    }

    async get(runId: string): Promise<JobRun | null> {
        const result = await this.sql.query(
            `select ${COLUMNS} from asgard_job_runs where id = $1`, [runId]);

        return result.rows.length === 0 ? null : toJobRun(result.rows[0]);
    }

    // Settling and chaining are one statement rather than a transaction. A
    // single statement is already atomic, so the successor cannot be lost to a
    // crash in the gap, and the store needs nothing from the client beyond
    // query. Selecting from the CTE means the insert only happens if the
    // settling update actually matched a row.
    private async settle(
        runId: string,
        status: JobStatus,
        error: string | null,
        next: JobRunRequest | null,
        nowMs: number
    ): Promise<void> {
        if (next === null) {
            await this.sql.query(
                `update asgard_job_runs
                 set status = $2,
                     last_error = coalesce($3, last_error),
                     lease_owner = null,
                     lease_expires_at_ms = null,
                     updated_at_ms = $4
                 where id = $1`,
                [runId, status, error, nowMs]);
            return;
        }

        await this.sql.query(
            `with settled as (
                 update asgard_job_runs
                 set status = $2,
                     last_error = coalesce($3, last_error),
                     lease_owner = null,
                     lease_expires_at_ms = null,
                     updated_at_ms = $4
                 where id = $1
                 returning id
             )
             insert into asgard_job_runs (${INSERT_COLUMNS})
             select $5, $6, $7::jsonb, $8, $9, 'pending', 0, $10, $9, $9
             from settled
             on conflict (idempotency_key) do nothing`,
            [
                runId,
                status,
                error,
                nowMs,
                next.scheduleId ?? null,
                next.handler,
                serialize(next.payload),
                next.idempotencyKey ?? null,
                next.runAtMs,
                next.maxAttempts ?? 1
            ]);
    }
}

function serialize(payload: unknown): string | null {
    return payload === undefined || payload === null ? null : JSON.stringify(payload);
}

// bigint columns arrive as strings from node-postgres, because a bigint can
// exceed what a JavaScript number holds exactly. Epoch milliseconds and our row
// ids are both well inside the safe range, so converting is fine here.
function toJobRun(row: any): JobRun {
    return {
        id: String(row.id),
        scheduleId: row.schedule_id,
        handler: row.handler,
        payload: row.payload,
        idempotencyKey: row.idempotency_key,
        runAtMs: Number(row.run_at_ms),
        status: row.status as JobStatus,
        attempt: row.attempt,
        maxAttempts: row.max_attempts,
        leaseOwner: row.lease_owner,
        leaseExpiresAtMs: row.lease_expires_at_ms === null ? null : Number(row.lease_expires_at_ms),
        lastError: row.last_error,
        createdAtMs: Number(row.created_at_ms),
        updatedAtMs: Number(row.updated_at_ms)
    };
}
