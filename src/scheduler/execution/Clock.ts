// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

// Time is injected rather than read directly so that backoff, lease expiry and
// the worker loop can be tested without waiting. Nothing in the scheduler calls
// Date.now outside this file.
export interface Clock {
    // Wall clock epoch milliseconds. Stored instants and lease deadlines are
    // compared across processes, so they have to be wall clock rather than
    // monotonic.
    nowMs(): number;

    // Waits roughly ms, resolving early if the signal aborts. Callers must
    // re-read nowMs afterwards rather than assuming the wait landed exactly,
    // because the process may have been suspended or the clock stepped.
    sleep(ms: number, signal?: AbortSignal): Promise<void>;
}

export const systemClock: Clock = {
    nowMs: () => Date.now(),

    sleep(ms: number, signal?: AbortSignal): Promise<void> {
        if (ms <= 0 || signal?.aborted) return Promise.resolve();

        return new Promise<void>(resolve => {
            const done = () => {
                clearTimeout(timer);
                signal?.removeEventListener("abort", done);
                resolve();
            };
            // setTimeout is driven by a monotonic source, so a wall clock step
            // cannot stretch or collapse the wait.
            const timer = setTimeout(done, ms);
            signal?.addEventListener("abort", done);
        });
    }
};

// Virtual time for tests. Sleeping advances the clock and returns immediately,
// so a worker loop runs to completion instantly and deterministically.
export class FakeClock implements Clock {
    private current: number;

    constructor(startMs: number) {
        this.current = startMs;
    }

    nowMs(): number {
        return this.current;
    }

    advance(ms: number): void {
        this.current += ms;
    }

    sleep(ms: number): Promise<void> {
        if (ms > 0) this.current += ms;
        return Promise.resolve();
    }
}
