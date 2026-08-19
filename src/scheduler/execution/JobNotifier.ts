// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Optional. Without one a worker discovers new work by polling, which is fine to
// thousands of jobs a second but means a job enqueued just after a poll waits
// most of an interval before it runs. A notifier lets the worker be woken
// instead.
//
// Polling always remains the floor. A missed notification, a dropped connection
// or a notifier that throws costs latency, never correctness, which is what
// makes this safe to bolt on.
export interface JobNotifier {
    // Wake any worker waiting for work. Called after enqueueing something that
    // is already due.
    notify(): Promise<void>;

    // Wait up to timeoutMs, returning early when notified or when the signal
    // aborts. Must not reject: a notifier that cannot reach its backend should
    // fall back to waiting out the timeout.
    wait(timeoutMs: number, signal?: AbortSignal): Promise<void>;
}

// Process local notifier. Useful with InMemoryJobStore, and the deterministic
// stand-in for a worker's wake-up path in tests.
export class InMemoryNotifier implements JobNotifier {
    private waiters: Array<() => void> = [];

    async notify(): Promise<void> {
        const woken = this.waiters;
        this.waiters = [];
        for (const wake of woken) wake();
    }

    wait(timeoutMs: number, signal?: AbortSignal): Promise<void> {
        if (signal?.aborted) return Promise.resolve();

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
}
