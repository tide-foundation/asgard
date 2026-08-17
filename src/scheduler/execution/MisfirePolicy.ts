// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

export enum MisfirePolicy {
    // Catch up with a single run, whatever was missed. The right default: after
    // an outage you usually want the job to happen, once, not sixty times.
    FireOnce = "fire_once",
    // Enqueue every missed occurrence.
    FireAll = "fire_all",
    // Abandon what was missed and wait for the next occurrence.
    Skip = "skip"
}
