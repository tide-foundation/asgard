// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { JobRun, JobRunRequest, JobStatus } from "./JobRun";
import { JobStore } from "./JobStore";

// Reference implementation and test double. Everything survives only as long as
// the process, so use it for local timers and for exercising a worker, not for
// work that must not be lost.
//
// JavaScript runs these methods to completion without interleaving, so the
// atomicity a durable store gets from a transaction is free here.
export class InMemoryJobStore implements JobStore {
    private readonly runs = new Map<string, JobRun>();
    private readonly keys = new Set<string>();
    private sequence = 0;

    // Ids are sequential rather than random so test failures are readable.
    private nextId(): string {
        this.sequence += 1;
        return `run-${this.sequence}`;
    }

    async enqueue(request: JobRunRequest): Promise<JobRun | null> {
        const key = request.idempotencyKey ?? null;
        if (key !== null && this.keys.has(key)) return null;

        const now = request.runAtMs;
        const run: JobRun = {
            id: this.nextId(),
            scheduleId: request.scheduleId ?? null,
            handler: request.handler,
            payload: request.payload,
            idempotencyKey: key,
            runAtMs: request.runAtMs,
            status: JobStatus.Pending,
            attempt: 0,
            maxAttempts: request.maxAttempts ?? 1,
            leaseOwner: null,
            leaseExpiresAtMs: null,
            lastError: null,
            createdAtMs: now,
            updatedAtMs: now
        };

        this.runs.set(run.id, run);
        if (key !== null) this.keys.add(key);
        return run;
    }

    async claimDue(owner: string, nowMs: number, leaseMs: number, max: number): Promise<JobRun[]> {
        const due = Array.from(this.runs.values())
            .filter(r => r.status === JobStatus.Pending && r.runAtMs <= nowMs)
            .sort((a, b) => a.runAtMs - b.runAtMs || compareIds(a.id, b.id))
            .slice(0, Math.max(0, max));

        return due.map(run => {
            const claimed: JobRun = {
                ...run,
                status: JobStatus.Leased,
                attempt: run.attempt + 1,
                leaseOwner: owner,
                leaseExpiresAtMs: nowMs + leaseMs,
                updatedAtMs: nowMs
            };
            this.runs.set(run.id, claimed);
            return claimed;
        });
    }

    async heartbeat(runId: string, leaseUntilMs: number): Promise<boolean> {
        const run = this.runs.get(runId);
        if (run === undefined || run.status !== JobStatus.Leased) return false;

        this.runs.set(runId, { ...run, leaseExpiresAtMs: leaseUntilMs, updatedAtMs: leaseUntilMs });
        return true;
    }

    async complete(runId: string, next: JobRunRequest | null, nowMs: number): Promise<void> {
        const run = this.runs.get(runId);
        if (run === undefined) return;

        this.runs.set(runId, {
            ...run,
            status: JobStatus.Succeeded,
            leaseOwner: null,
            leaseExpiresAtMs: null,
            updatedAtMs: nowMs
        });

        // Same call, so a caller can never settle without chaining.
        if (next !== null) await this.enqueue(next);
    }

    async retry(runId: string, error: string, runAtMs: number, nowMs: number): Promise<void> {
        const run = this.runs.get(runId);
        if (run === undefined) return;

        this.runs.set(runId, {
            ...run,
            status: JobStatus.Pending,
            runAtMs,
            lastError: error,
            leaseOwner: null,
            leaseExpiresAtMs: null,
            updatedAtMs: nowMs
        });
    }

    async deadLetter(
        runId: string, error: string, next: JobRunRequest | null, nowMs: number): Promise<void> {
        const run = this.runs.get(runId);
        if (run === undefined) return;

        this.runs.set(runId, {
            ...run,
            status: JobStatus.Dead,
            lastError: error,
            leaseOwner: null,
            leaseExpiresAtMs: null,
            updatedAtMs: nowMs
        });

        if (next !== null) await this.enqueue(next);
    }

    async reapExpired(nowMs: number): Promise<number> {
        let reaped = 0;

        for (const run of Array.from(this.runs.values())) {
            if (run.status !== JobStatus.Leased) continue;
            if (run.leaseExpiresAtMs !== null && run.leaseExpiresAtMs > nowMs) continue;

            this.runs.set(run.id, {
                ...run,
                status: JobStatus.Pending,
                leaseOwner: null,
                leaseExpiresAtMs: null,
                lastError: "lease expired",
                updatedAtMs: nowMs
            });
            reaped += 1;
        }

        return reaped;
    }

    async get(runId: string): Promise<JobRun | null> {
        return this.runs.get(runId) ?? null;
    }

    // Inspection helpers for tests and local debugging. Not part of JobStore.

    all(): JobRun[] {
        return Array.from(this.runs.values());
    }

    byStatus(status: JobStatus): JobRun[] {
        return this.all().filter(r => r.status === status);
    }

    countByStatus(status: JobStatus): number {
        return this.byStatus(status).length;
    }
}

// Sequential ids compared numerically so run-10 sorts after run-9.
function compareIds(a: string, b: string): number {
    return Number(a.slice(4)) - Number(b.slice(4));
}
