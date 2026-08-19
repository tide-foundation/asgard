// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

export enum JitterMode {
    // Exact backoff. Predictable, but a fleet that failed together retries
    // together, which is how a struggling dependency gets knocked over.
    None = "none",
    // Anywhere in [0, delay]. Spreads retries the widest.
    Full = "full",
    // Anywhere in [delay/2, delay]. Keeps most of the backoff while still
    // breaking up the herd.
    Equal = "equal"
}

export interface RetryPolicy {
    // Total attempts including the first. 1 means never retry.
    maxAttempts: number;
    baseMs: number;
    capMs: number;
    multiplier: number;
    jitter: JitterMode;
}

export const DEFAULT_RETRY_POLICY: RetryPolicy = {
    maxAttempts: 5,
    baseMs: 1_000,
    capMs: 300_000,
    multiplier: 2,
    jitter: JitterMode.Full
};

// A run that has used up attempt attempts may be tried again when this is true.
export function shouldRetry(policy: RetryPolicy, attempt: number): boolean {
    return attempt < policy.maxAttempts;
}

// Delay before the next attempt, where attempt is the one that just failed and
// counts from 1. The random source is injected so tests can pin the result.
export function retryDelayMs(
    policy: RetryPolicy, attempt: number, random: () => number = Math.random): number {

    const exponent = Math.max(0, attempt - 1);
    const raw = policy.baseMs * Math.pow(policy.multiplier, exponent);

    // Cap before jitter, otherwise the cap stops being an upper bound.
    const capped = Math.min(raw, policy.capMs);

    switch (policy.jitter) {
        case JitterMode.None: return Math.round(capped);
        case JitterMode.Full: return Math.round(capped * random());
        case JitterMode.Equal: return Math.round(capped / 2 + (capped / 2) * random());
    }
}

// Thrown by a handler when the work can never succeed, for example a malformed
// payload. Skips the remaining attempts and sends the run straight to dead.
export class PermanentJobError extends Error {
    constructor(message: string) {
        super(message);
        this.name = "PermanentJobError";
    }
}
