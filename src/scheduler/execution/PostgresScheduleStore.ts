// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { specFromJson, specToJson } from "../expression/Serialization";
import { MisfirePolicy } from "./MisfirePolicy";
import { migrate } from "./Migrations";
import { SqlClient } from "./PostgresJobStore";
import { ScheduleRecord, ScheduleStore, ScheduleUpsert } from "./ScheduleStore";

const COLUMNS = `name, handler, payload, expr, spec, enabled, misfire, max_attempts,
    next_fire_at_ms, last_fire_at_ms, updated_at_ms`;

// Durable schedules. Uses the same client as PostgresJobStore, and the same
// schema file creates both tables, so pointing either one at a pool is enough.
export class PostgresScheduleStore implements ScheduleStore {
    constructor(private readonly sql: SqlClient) { }

    // Both tables come from the same migrations, so this is the job store's
    // schema. Having it here means createScheduler can bring a schedule store up
    // on its own.
    async ensureSchema(): Promise<number[]> {
        return migrate(this.sql);
    }

    // One statement. On conflict the definition is updated but enabled is left
    // alone, so a redeploy cannot silently resume something an operator paused,
    // and next_fire_at_ms is only reset when the spec actually changed, so a
    // redeploy does not skip or repeat an occurrence either.
    //
    // Every SET expression sees the pre-update row, so comparing against
    // asgard_schedules.spec here is comparing against the stored spec.
    async upsert(input: ScheduleUpsert, nowMs: number): Promise<ScheduleRecord> {
        const result = await this.sql.query(
            `insert into asgard_schedules
                 (name, handler, payload, expr, spec, misfire, max_attempts,
                  next_fire_at_ms, created_at_ms, updated_at_ms)
             values ($1, $2, $3::jsonb, $4, $5::jsonb, $6, $7, $8, $9, $9)
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
             returning ${COLUMNS}`,
            [
                input.name,
                input.handler,
                input.payload === undefined || input.payload === null
                    ? null
                    : JSON.stringify(input.payload),
                input.expr,
                JSON.stringify(specToJson(input.spec)),
                input.misfire,
                input.maxAttempts,
                input.nextFireAtMs,
                nowMs
            ]);

        return toRecord(result.rows[0]);
    }

    async listDue(nowMs: number, limit: number): Promise<ScheduleRecord[]> {
        const result = await this.sql.query(
            `select ${COLUMNS} from asgard_schedules
             where enabled and next_fire_at_ms is not null and next_fire_at_ms <= $1
             order by next_fire_at_ms, name
             limit $2`,
            [nowMs, Math.max(0, limit)]);

        return result.rows.map(toRecord);
    }

    async list(): Promise<ScheduleRecord[]> {
        const result = await this.sql.query(
            `select ${COLUMNS} from asgard_schedules order by name`);
        return result.rows.map(toRecord);
    }

    async get(name: string): Promise<ScheduleRecord | null> {
        const result = await this.sql.query(
            `select ${COLUMNS} from asgard_schedules where name = $1`, [name]);

        return result.rows.length === 0 ? null : toRecord(result.rows[0]);
    }

    async advance(
        name: string, nextFireAtMs: number | null, lastFireAtMs: number, nowMs: number
    ): Promise<void> {
        await this.sql.query(
            `update asgard_schedules
             set next_fire_at_ms = $2, last_fire_at_ms = $3, updated_at_ms = $4
             where name = $1`,
            [name, nextFireAtMs, lastFireAtMs, nowMs]);
    }

    async setEnabled(name: string, enabled: boolean, nowMs: number): Promise<boolean> {
        const result = await this.sql.query(
            `update asgard_schedules set enabled = $2, updated_at_ms = $3 where name = $1`,
            [name, enabled, nowMs]);

        return (result.rowCount ?? 0) > 0;
    }

    async remove(name: string): Promise<boolean> {
        const result = await this.sql.query(
            `delete from asgard_schedules where name = $1`, [name]);

        return (result.rowCount ?? 0) > 0;
    }
}

function toRecord(row: any): ScheduleRecord {
    return {
        name: row.name,
        handler: row.handler,
        payload: row.payload,
        expr: row.expr,
        spec: specFromJson(row.spec),
        enabled: row.enabled,
        misfire: row.misfire as MisfirePolicy,
        maxAttempts: row.max_attempts,
        nextFireAtMs: row.next_fire_at_ms === null ? null : Number(row.next_fire_at_ms),
        lastFireAtMs: row.last_fire_at_ms === null ? null : Number(row.last_fire_at_ms),
        updatedAtMs: Number(row.updated_at_ms)
    };
}
