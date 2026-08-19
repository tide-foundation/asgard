// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { JobDefinition } from "./JobDefinition";

export interface JobContext {
    readonly runId: string;
    // Counts from 1. Useful for logging and for handlers that behave differently
    // on a retry.
    readonly attempt: number;
    readonly maxAttempts: number;
    // Aborted on worker shutdown.
    readonly signal: AbortSignal;
    // Extends the lease. A worker started with start renews leases for you, so
    // this is only needed when driving tick directly, or to check whether the
    // lease is still held. Returns false when it is not, at which point the
    // handler should stop because another worker has taken the run.
    heartbeat(): Promise<boolean>;
}

// Jobs are looked up by name at execution time rather than captured as closures,
// because a durable store holds a name and a payload, not a function. That is
// also what lets a run enqueued by one process execute in another.
export class HandlerRegistry {
    // The payload type is erased here on purpose. It is recovered at dispatch by
    // the definition itself, which is the only thing that knows it.
    private readonly jobs = new Map<string, JobDefinition<any>>();

    register<TPayload>(definition: JobDefinition<TPayload>): this {
        if (this.jobs.has(definition.name)) {
            throw new Error(`job '${definition.name}' is already registered`);
        }
        this.jobs.set(definition.name, definition);
        return this;
    }

    registerAll(definitions: readonly JobDefinition<any>[]): this {
        for (const definition of definitions) this.register(definition);
        return this;
    }

    resolve(name: string): JobDefinition<any> | undefined {
        return this.jobs.get(name);
    }

    has(name: string): boolean {
        return this.jobs.has(name);
    }

    names(): string[] {
        return Array.from(this.jobs.keys());
    }
}
