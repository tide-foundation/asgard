// Copyright (c) Tide Foundation Limited. All rights reserved.
// Licensed under the Tide Community Open Code License. See LICENSE in the project root.

using Tide.Asgard.Scheduler.Execution;

namespace Tide.Asgard.Scheduler.AspNetCore;

// A job written as a class rather than a lambda, so it can take constructor
// dependencies. The scheduler resolves one of these from a fresh scope for every
// run, which is what makes a scoped DbContext or a per-request-style service
// safe to inject.
public interface IJobHandler<in TPayload>
{
	Task HandleAsync(TPayload payload, JobContext context);
}
