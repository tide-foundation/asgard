// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { JobRun } from "./JobRun";

export type RunOutcome = "succeeded" | "retried" | "dead";

export interface RunStartedEvent {
    readonly run: JobRun;
    readonly atMs: number;
}

export interface RunFinishedEvent {
    readonly run: JobRun;
    readonly outcome: RunOutcome;
    readonly durationMs: number;
    // Present when the outcome is not succeeded.
    readonly error?: unknown;
    // When the next attempt is due, for a retry. Null for a terminal outcome.
    readonly nextAttemptAtMs?: number | null;
}

export interface TickFinishedEvent {
    readonly result: TickCounts;
    readonly durationMs: number;
}

// Structurally the same as TickResult, declared here so this file does not have
// to import from Worker and create a cycle.
export interface TickCounts {
    readonly reaped: number;
    readonly materialized: number;
    readonly claimed: number;
    readonly succeeded: number;
    readonly retried: number;
    readonly dead: number;
    readonly purged: number;
}

// Somewhere to hang a log line, a metric or a trace span. Every method is
// optional, so implement only what you need.
//
// Callbacks are synchronous on purpose. Awaiting an observer would put the
// caller's logging or tracing on the critical path of every run, and a slow one
// would quietly become a throughput problem. Anything expensive should be
// queued by the observer itself.
//
// A callback that throws is reported through onError and otherwise ignored.
// Observing work must not be able to break it.
export interface JobObserver {
    runStarted?(event: RunStartedEvent): void;
    runFinished?(event: RunFinishedEvent): void;
    tickFinished?(event: TickFinishedEvent): void;
}
