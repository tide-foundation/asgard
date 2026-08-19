// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { specToString } from "../expression/Serialization";
import { ScheduleRecord, ScheduleStore, ScheduleUpsert } from "./ScheduleStore";

// Default schedule store. Schedules live only as long as the process, which is
// fine when they are declared in code and re-registered at startup. Swap in
// PostgresScheduleStore to have a pause survive a restart, or to add a schedule
// without a deploy.
export class InMemoryScheduleStore implements ScheduleStore {
    private readonly schedules = new Map<string, ScheduleRecord>();

    async upsert(input: ScheduleUpsert, nowMs: number): Promise<ScheduleRecord> {
        const existing = this.schedules.get(input.name);

        // Comparing the canonical spec rather than the expression text, so a
        // reworded expression that means the same thing does not disturb the
        // schedule's position in time.
        const specChanged =
            existing === undefined || specToString(existing.spec) !== specToString(input.spec);

        const record: ScheduleRecord = {
            name: input.name,
            handler: input.handler,
            payload: input.payload ?? null,
            expr: input.expr,
            spec: input.spec,
            enabled: existing?.enabled ?? true,
            misfire: input.misfire,
            maxAttempts: input.maxAttempts,
            nextFireAtMs: specChanged ? input.nextFireAtMs : existing.nextFireAtMs,
            lastFireAtMs: existing?.lastFireAtMs ?? null,
            updatedAtMs: nowMs
        };

        this.schedules.set(input.name, record);
        return record;
    }

    async listDue(nowMs: number, limit: number): Promise<ScheduleRecord[]> {
        return Array.from(this.schedules.values())
            .filter(s => s.enabled && s.nextFireAtMs !== null && s.nextFireAtMs <= nowMs)
            .sort((a, b) => (a.nextFireAtMs ?? 0) - (b.nextFireAtMs ?? 0))
            .slice(0, Math.max(0, limit));
    }

    async list(): Promise<ScheduleRecord[]> {
        return Array.from(this.schedules.values()).sort((a, b) => a.name.localeCompare(b.name));
    }

    async get(name: string): Promise<ScheduleRecord | null> {
        return this.schedules.get(name) ?? null;
    }

    async advance(
        name: string, nextFireAtMs: number | null, lastFireAtMs: number, nowMs: number
    ): Promise<void> {
        const existing = this.schedules.get(name);
        if (existing === undefined) return;

        this.schedules.set(name, { ...existing, nextFireAtMs, lastFireAtMs, updatedAtMs: nowMs });
    }

    async setEnabled(name: string, enabled: boolean, nowMs: number): Promise<boolean> {
        const existing = this.schedules.get(name);
        if (existing === undefined) return false;

        this.schedules.set(name, { ...existing, enabled, updatedAtMs: nowMs });
        return true;
    }

    async remove(name: string): Promise<boolean> {
        return this.schedules.delete(name);
    }
}
