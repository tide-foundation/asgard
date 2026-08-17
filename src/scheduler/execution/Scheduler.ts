// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

import { JobStore } from "./JobStore";
import { Worker, WorkerOptions } from "./Worker";

// A store that can create its own tables. Detected rather than required, so the
// convenience below works with any store without widening the JobStore contract
// with something only durable implementations need.
interface SchemaAware {
    ensureSchema(): Promise<void>;
}

function isSchemaAware(store: JobStore): store is JobStore & SchemaAware {
    return typeof (store as Partial<SchemaAware>).ensureSchema === "function";
}

// Everything needed to go from nothing to a running scheduler in one call:
//
//   const scheduler = await createScheduler({
//       store: new PostgresJobStore(pool),
//       jobs: [reconcileOrks],
//       schedules: [{ name: "nightly", expr: "on 03:00", job: reconcileOrks, payload: { realmId: "tide" } }]
//   });
//   scheduler.start();
//
// The only thing this adds over the constructor is applying the store's schema
// when it has one, which is the step most easily forgotten and the one whose
// absence fails at the least convenient moment.
export async function createScheduler(options: WorkerOptions): Promise<Worker> {
    if (isSchemaAware(options.store)) await options.store.ensureSchema();
    return new Worker(options);
}
