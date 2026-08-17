// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

export interface JobContext {
    readonly runId: string;
    // Counts from 1. Useful for logging and for handlers that behave differently
    // on a retry.
    readonly attempt: number;
    readonly maxAttempts: number;
    // Aborted on worker shutdown, and when the lease is lost.
    readonly signal: AbortSignal;
    // Extends the lease. A handler that may outlive the lease has to call this
    // periodically, otherwise the reaper will hand its run to another worker
    // while it is still running. Returns false when the lease is already gone,
    // at which point the handler should stop.
    heartbeat(): Promise<boolean>;
}

export type JobHandler = (payload: unknown, ctx: JobContext) => Promise<void> | void;

// Handlers are looked up by name at execution time rather than captured as
// closures, because a durable store holds a name and a payload, not a function.
// That is also what lets a run enqueued by one process execute in another.
export class HandlerRegistry {
    private readonly handlers = new Map<string, JobHandler>();

    register(name: string, handler: JobHandler): this {
        if (this.handlers.has(name)) {
            throw new Error(`handler '${name}' is already registered`);
        }
        this.handlers.set(name, handler);
        return this;
    }

    resolve(name: string): JobHandler | undefined {
        return this.handlers.get(name);
    }

    has(name: string): boolean {
        return this.handlers.has(name);
    }

    names(): string[] {
        return Array.from(this.handlers.keys());
    }
}
