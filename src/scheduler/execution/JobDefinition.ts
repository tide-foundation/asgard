// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { JobContext } from "./HandlerRegistry";

// One object that carries a job's name, its payload type and its handler, so
// registering it and enqueueing it cannot disagree. Pass the definition to both
// and the compiler checks the payload for you:
//
//   const reconcile = defineJob({
//       name: "reconcile-orks",
//       handler: (payload: { realmId: string }) => reconcile(payload.realmId)
//   });
//
//   worker.enqueue(reconcile, { realmId: "tide" });   // checked
//   worker.enqueue(reconcile, { realm: "tide" });     // compile error
export interface JobDefinition<TPayload = void> {
    readonly name: string;

    readonly handler: (payload: TPayload, ctx: JobContext) => Promise<void> | void;

    // Optional guard, run on dequeue rather than on enqueue. That is the useful
    // side, because it catches a payload written by an older deploy reaching a
    // handler that now expects a different shape. Throwing here is treated as
    // permanent: no amount of retrying will change the stored payload.
    readonly parse?: (raw: unknown) => TPayload;

    // Default attempt limit for runs of this job, overridable per enqueue.
    readonly maxAttempts?: number;
}

// Identity function whose only job is to infer TPayload from the handler, so
// callers write the payload type once.
export function defineJob<TPayload = void>(
    definition: JobDefinition<TPayload>): JobDefinition<TPayload> {
    return definition;
}

// Raised when a stored payload does not match what the handler expects.
export class PayloadError extends Error {
    constructor(jobName: string, cause: unknown) {
        super(`payload for '${jobName}' is not valid: ${cause instanceof Error ? cause.message : String(cause)}`);
        this.name = "PayloadError";
    }
}
