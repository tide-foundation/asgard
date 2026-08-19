// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { JobNotifier } from "./JobNotifier";
import { SqlClient } from "./PostgresJobStore";

export const JOB_CHANNEL = "asgard_jobs";

// LISTEN needs a connection of its own: notifications are delivered to the
// session that issued the LISTEN, so a pooled connection would deliver them to
// whichever caller happened to get that connection next. A node-postgres Client
// satisfies this as is, which is why nothing here imports pg.
export interface ListenClient {
    query(text: string): Promise<unknown>;
    on(event: string, listener: (...args: any[]) => void): unknown;
    removeListener?(event: string, listener: (...args: any[]) => void): unknown;
}

// Wakes workers across processes. NOTIFY goes out through the pool, LISTEN sits
// on a dedicated connection the host supplies already connected:
//
//   const listener = new Client({ connectionString });
//   await listener.connect();
//   const notifier = new PostgresNotifier(pool, listener);
export class PostgresNotifier implements JobNotifier {
    private listening = false;
    private waiters: Array<() => void> = [];

    constructor(
        private readonly sql: SqlClient,
        private readonly listener: ListenClient
    ) { }

    async notify(): Promise<void> {
        // Deliberately swallowed. A worker that cannot announce new work still
        // enqueued it, and the next poll finds it.
        try {
            await this.sql.query(`select pg_notify('${JOB_CHANNEL}', '')`);
        } catch {
            // Latency, not correctness.
        }
    }

    async wait(timeoutMs: number, signal?: AbortSignal): Promise<void> {
        if (signal?.aborted) return;

        try {
            await this.ensureListening();
        } catch {
            // Fall back to waiting out the timeout, which is what a worker
            // without a notifier does anyway.
        }

        return new Promise<void>(resolve => {
            let settled = false;

            const finish = () => {
                if (settled) return;
                settled = true;
                clearTimeout(timer);
                signal?.removeEventListener("abort", finish);
                this.waiters = this.waiters.filter(w => w !== finish);
                resolve();
            };

            const timer = setTimeout(finish, timeoutMs);
            signal?.addEventListener("abort", finish);
            this.waiters.push(finish);
        });
    }

    private async ensureListening(): Promise<void> {
        if (this.listening) return;
        this.listening = true;

        this.listener.on("notification", (message: { channel?: string }) => {
            if (message?.channel !== JOB_CHANNEL) return;
            this.wake();
        });

        // A dropped connection means notifications stop arriving. Waking
        // everyone and re-listening next time keeps that to a latency problem.
        this.listener.on("error", () => {
            this.listening = false;
            this.wake();
        });

        await this.listener.query(`listen ${JOB_CHANNEL}`);
    }

    private wake(): void {
        const woken = this.waiters;
        this.waiters = [];
        for (const resolve of woken) resolve();
    }
}
