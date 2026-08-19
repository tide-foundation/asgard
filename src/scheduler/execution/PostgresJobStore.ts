// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { JobRun, JobRunRequest, JobStatus } from "./JobRun";
import { JobStore, JobStoreStats } from "./JobStore";
import { migrate } from "./Migrations";

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

const COLUMNS = `id, schedule_id, handler, payload, idempotency_key, run_at_ms, status,
    attempt, max_attempts, lease_owner, lease_expires_at_ms, last_error,
    created_at_ms, updated_at_ms`;

const INSERT_COLUMNS = `schedule_id, handler, payload, idempotency_key, run_at_ms,
    status, attempt, max_attempts, created_at_ms, updated_at_ms`;

export class PostgresJobStore implements JobStore {
    constructor(private readonly sql: SqlClient) { }

    // Applies any migrations this database has not seen. Safe to call on every
    // startup, and safe to call from several processes at once.
    async ensureSchema(): Promise<number[]> {
        return migrate(this.sql);
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
    async claimDue(
        owner: string,
        nowMs: number,
        leaseMs: number,
        max: number,
        handlers?: readonly string[]
    ): Promise<JobRun[]> {
        if (max <= 0) return [];
        // An empty allow list means this worker can run nothing, which is not
        // the same as no filter at all.
        if (handlers !== undefined && handlers.length === 0) return [];

        const result = await this.sql.query(
            `update asgard_job_runs
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
             returning ${COLUMNS}`,
            [owner, nowMs, leaseMs, max, handlers === undefined ? null : Array.from(handlers)]);

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

    // Deleting through a bounded subquery rather than one sweeping statement, so
    // a long backlog is cleared in chunks instead of a single delete holding
    // locks across the whole table.
    async purgeSettled(beforeMs: number, limit: number, includeDead = false): Promise<number> {
        if (limit <= 0) return 0;

        const statuses = includeDead
            ? [JobStatus.Succeeded, JobStatus.Dead]
            : [JobStatus.Succeeded];

        const result = await this.sql.query(
            `delete from asgard_job_runs
             where id in (
                 select id from asgard_job_runs
                 where status = any($1) and updated_at_ms < $2
                 order by updated_at_ms, id
                 limit $3
             )`,
            [statuses, beforeMs, limit]);

        return result.rowCount ?? 0;
    }

    async cancel(runId: string, nowMs: number): Promise<boolean> {
        const result = await this.sql.query(
            `update asgard_job_runs
             set status = 'cancelled',
                 lease_owner = null,
                 lease_expires_at_ms = null,
                 last_error = 'cancelled',
                 updated_at_ms = $2
             where id = $1 and status in ('pending', 'leased')`,
            [runId, nowMs]);

        return (result.rowCount ?? 0) > 0;
    }

    async requeue(runId: string, runAtMs: number, nowMs: number): Promise<boolean> {
        const result = await this.sql.query(
            `update asgard_job_runs
             set status = 'pending',
                 run_at_ms = $2,
                 attempt = 0,
                 last_error = null,
                 lease_owner = null,
                 lease_expires_at_ms = null,
                 updated_at_ms = $3
             where id = $1 and status in ('dead', 'cancelled')`,
            [runId, runAtMs, nowMs]);

        return (result.rowCount ?? 0) > 0;
    }

    async stats(nowMs: number): Promise<JobStoreStats> {
        const result = await this.sql.query(
            `select
                 count(*) filter (where status = 'pending')   as pending,
                 count(*) filter (where status = 'leased')    as leased,
                 count(*) filter (where status = 'succeeded') as succeeded,
                 count(*) filter (where status = 'dead')      as dead,
                 count(*) filter (where status = 'cancelled') as cancelled,
                 coalesce(max($1::bigint - run_at_ms)
                     filter (where status = 'pending' and run_at_ms <= $1), 0) as oldest
             from asgard_job_runs`,
            [nowMs]);

        const row = result.rows[0];
        return {
            pending: Number(row.pending),
            leased: Number(row.leased),
            succeeded: Number(row.succeeded),
            dead: Number(row.dead),
            cancelled: Number(row.cancelled),
            oldestPendingAgeMs: Number(row.oldest)
        };
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
