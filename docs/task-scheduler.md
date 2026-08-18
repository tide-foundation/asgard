# Task scheduler

Runs jobs on a schedule, in TypeScript and .NET, from one shared expression
language. The core of each has no third party dependencies.

- **Expression** — text in, next fire time out. Pure functions, no state.
- **Execution** — a store holds jobs, a worker claims and runs them, with leases,
  retries and backoff.

Start in memory, swap in Postgres when it has to survive a restart. Nothing else
changes.

## Quick start

```ts
import { defineJob, createScheduler, PostgresJobStore } from "asgard-tide";
import { Pool } from "pg";

const reconcileOrks = defineJob({
    name: "reconcile-orks",
    handler: async (payload: { realmId: string }, ctx) => await reconcile(payload.realmId)
});

const scheduler = await createScheduler({
    store: new PostgresJobStore(new Pool({ connectionString })),
    jobs: [reconcileOrks],
    schedules: [{
        name: "nightly",
        expr: "on 03:00 tz=Australia/Sydney",
        job: reconcileOrks,
        payload: { realmId: "tide" }
    }]
});

await scheduler.enqueue(reconcileOrks, { realmId: "adhoc" });  // payload is type checked
scheduler.start();
```

```csharp
builder.Services.AddAsgardScheduler(scheduler => scheduler
    .UseStore(_ => PostgresJobStore.Create(connectionString))
    .UseLogging()
    .AddJob<ReconcileOrks, ReconcilePayload>("reconcile-orks")
    .AddSchedule("nightly", "on 03:00 tz=Australia/Sydney", "reconcile-orks",
        new ReconcilePayload("tide")));
```

`createScheduler` / `AddAsgardScheduler` applies the database schema for you.
Use `InMemoryJobStore` instead for local timers and tests.

> **The contract is at-least-once.** A worker can die after a side effect and
> before recording it, so handlers must be idempotent. No store fixes this; it is
> a property of running work outside the transaction that records it.

## Schedule expressions

Most schedules are one line from this table.

| You want | Write |
|---|---|
| Every day at 3am | `on 03:00` |
| Twice a day | `on hour=3,15` |
| Every 15 minutes | `on minute=*/15` |
| Weekdays at 9:30am | `on 09:30 dow=mon-fri` |
| Business hours, hourly | `on hour=9-17 minute=0 dow=mon-fri` |
| First of the month | `on day=1 00:15` |
| Last day of the month | `on day=last 23:55` |
| Second Tuesday | `on nth=2 dow=tue 10:00` |
| Quarterly | `on month=1,4,7,10 day=1` |
| Another timezone | `on 03:00 tz=Australia/Sydney` |
| 5 minutes after each run ends | `every 5m` |
| On a 5 minute grid regardless | `every 5m from 2026-01-01T00:00:00Z` |
| Once, at a fixed moment | `at 2026-09-01T03:00:00Z` |

Three kinds, told apart by the leading keyword:

```
on    [HH:MM] [<field>=<value> ...]     calendar
every <duration> [from <instant>] [jitter <duration>]
at    <iso-instant>
```

### Fields

| Field | Range | Notes |
|---|---|---|
| `second` `minute` | 0-59 | |
| `hour` | 0-23 | a bare `HH:MM` sets hour and minute |
| `day` | 1-31 or `last` | |
| `dow` | 0-6 or `sun`..`sat` | Sunday is 0 |
| `month` | 1-12 or `jan`..`dec` | |
| `nth` | 1-5 or `last` | which occurrence of `dow` in the month |
| `tz` | IANA zone id | defaults to `UTC` |
| `dstgap` | `fire_at_gap_end`, `skip` | when the clock skips forward |
| `dstfold` | `fire_first`, `fire_last` | when the clock repeats an hour |

Values take `*`, `a`, `a,b,c`, `a-b`, `*/n`, `a-b/n` and `a/n`. Durations are
`ms`, `s`, `m`, `h`, `d`, each at most once and descending, so `1h30m` parses
and `30m1h` does not.

### Rules worth knowing

**Defaulting is explicit.** Fields finer than the finest one you named collapse
to their floor; coarser fields stay open. So `on hour=3` is 03:00:00 daily, not
every second during hour 3.

**Ambiguity is rejected, not guessed.** Restricting both `day` and `dow` gives
`E_DAY_AMBIGUOUS` rather than cron's surprising OR. `nth` without `dow` gives
`E_NTH_WITHOUT_DOW`. Parse errors carry a stable code and a character offset, and
both runtimes emit the same code for the same input.

**Impossible dates are skipped, not clamped.** `on day=31` runs only in months
that have one; `on day=29 month=2` runs on leap days; `on day=30 month=2` returns
null forever. The search bound is a full 400 year Gregorian cycle, which is the
period on which the calendar including weekday alignment repeats.

**Daylight saving is a policy, not a guess.** A wall clock that does not exist
fires at the gap end by default; one that happens twice fires on the first pass.
Timezone data comes from the platform, `TimeZoneInfo` on .NET and
`Intl.DateTimeFormat` on Node.

## Jobs and payloads

A job definition ties a name, a payload type and a handler together, so
enqueueing cannot disagree with the handler that receives it. Definitions
register themselves when enqueued or scheduled.

Payloads always travel as JSON, whichever store is in use — the in-memory store
round trips them deliberately, so a job cannot pass in tests and then meet a
`Date` turned into a string against Postgres. .NET deserializes into the
definition's type; TypeScript passes the raw value through unless the definition
supplies `parse`.

`parse` runs on **dequeue**, which is the useful side: it catches a payload
written by an older deploy meeting a handler that has since changed shape. A
payload that will not convert is dead lettered immediately, because no number of
attempts changes what is already stored.

`enqueueByName` is the escape hatch for an admin endpoint holding only a name.

## Durability

Swap the store. The worker, handlers and schedules are untouched.

```ts
const store = new PostgresJobStore(pool);
const scheduleStore = new PostgresScheduleStore(pool);   // optional, see below
```

```csharp
await using var store = PostgresJobStore.Create(connectionString);
```

Claiming uses `SELECT ... FOR UPDATE SKIP LOCKED`, so workers step over rows
their peers hold rather than blocking: replicas add throughput, not contention,
and no run is handed out twice. Settling a run and enqueueing its successor are a
single statement, so a crash cannot lose a recurring schedule.

**Schedules stay in memory unless you give it a schedule store.** That is right
when they are declared in code — the runs they materialize are already durable,
and an idempotency key stops replicas duplicating an occurrence. Use
`PostgresScheduleStore` when a pause must survive a restart, or to add a schedule
without a deploy.

### Migrations

The schema is versioned in [`sql/migrations/`](../sql/migrations/) and recorded
in `asgard_schema_migrations`. `ensureSchema` applies whatever is missing and
returns the versions it applied. Safe on every startup and from several processes
at once.

- **Append only.** Never edit a migration that has shipped; add the next number.
- **Each is safe to apply twice**, so a crash between applying and recording one
  cannot leave the schema unrepeatable.
- **Concurrent migrators are serialized** by an advisory lock, including the
  bootstrap of the migrations table itself — `create table if not exists` is not
  atomic against another session doing the same thing.

## Operating it

### Retries

A failed attempt returns the run to pending with a later time, so `attempt`
survives rather than starting over.

```
delay = min(baseMs * multiplier^(attempt-1), capMs), then jitter
```

Jitter is `none`, `full` (`[0, delay]`) or `equal` (`[delay/2, delay]`); the
default is full, because a fleet that failed together otherwise retries together.
The cap applies before jitter so it stays an upper bound.

`PermanentJobError` / `PermanentJobException` skips the remaining attempts. A
recurring schedule survives a run that dies: the successor is enqueued in the
same call that settles the failure.

### Leases

A claimed run is leased. If a worker dies, the reaper returns the run to pending
once the lease expires — that is what makes a crash recoverable, and also why the
contract is at-least-once. `start` renews the leases of in-flight runs for you,
alongside the tick loop rather than inside it, since a tick is blocked on the
very handlers that need renewing.

### Missed occurrences

| `misfire` | Behaviour |
|---|---|
| `fire_once` | Catch up with a single run. The default: after an outage you want the job to happen, once. |
| `fire_all` | Enqueue every missed occurrence. |
| `skip` | Abandon what was missed. |

### Mixed fleets

A worker claims only runs whose handler it has registered, so a run waits for a
process that can execute it. Set `claimOnlyRegisteredHandlers` to false when one
process owns every handler and an unknown name is a bug.

### Retention

```ts
retention: { afterMs: 7 * 86_400_000, everyMs: 3_600_000 }
```

Deletes settled runs in batches. Succeeded only unless `includeDead` is set,
because a dead run is evidence something never ran. Set `afterMs` comfortably
longer than the period of anything you schedule: purging a run drops its
idempotency key with it.

### Admin

```ts
await worker.listSchedules();
await worker.pauseSchedule("nightly");     // survives a restart with a schedule store
await worker.resumeSchedule("nightly");
await worker.triggerSchedule("nightly");   // runs now, timetable untouched
await worker.removeSchedule("nightly");
await worker.cancelRun(runId);             // pending or leased -> cancelled
await worker.requeueRun(runId);            // dead or cancelled -> pending
```

.NET has the same surface with `Async` names. Everything returns `false` rather
than throwing when there is nothing to act on.

**Re-registering a schedule preserves a pause** and keeps its place in time
unless the expression itself changed, so a redeploy cannot silently resume
something an operator stopped.

### Metrics and logs

```ts
const { pending, leased, succeeded, dead, cancelled, oldestPendingAgeMs } = await worker.stats();
```

Alert on `oldestPendingAgeMs`. Queue depth lies — a large batch looks exactly
like an outage — whereas a rising oldest age only ever means work is not being
picked up.

For per-run visibility pass an `observer`; every method is optional.
`runFinished` carries the outcome, duration, error and next attempt time.
Correlate with `runStarted` on `run.id` to wrap a trace span around a run. On
.NET, `UseLogging()` gives you a line per run through `ILogger`.

Callbacks are synchronous on purpose — awaiting one would put your logging on the
critical path of every run — and a callback that throws is reported through
`onError` and cannot break the work it is watching.

### Latency

By default a worker polls, so a job enqueued just after a poll waits most of an
interval. Give it a notifier and it gets woken instead:

```ts
const listener = new Client({ connectionString });   // LISTEN needs its own session
await listener.connect();
notifier: new PostgresNotifier(pool, listener)
```

**Polling stays the floor.** A missed notification, a dropped connection or a
notifier that throws costs latency and never correctness, which is what makes
this safe to add to a running system.

## ASP.NET Core

`Tide.Asgard.Scheduler.AspNetCore` wires everything into a host and runs it for
the application's lifetime. Jobs are classes, so they take constructor
dependencies:

```csharp
public sealed class ReconcileOrks(OrkDbContext db) : IJobHandler<ReconcilePayload>
{
    public async Task HandleAsync(ReconcilePayload payload, JobContext context)
        => await db.ReconcileAsync(payload.RealmId, context.CancellationToken);
}
```

**Each run gets its own scope**, so a scoped `DbContext` behaves the way it does
in a request rather than being shared across the process.

Schema and schedules are applied at startup, not while the container is built, so
an unreachable database or a schedule naming an unregistered job fails startup
loudly. Shutdown drains: the hosted service waits for handlers already running.

The `Worker` is a singleton, so the admin surface is injectable:

```csharp
app.MapPost("/admin/schedules/{name}/pause", async (string name, Worker scheduler)
    => await scheduler.PauseScheduleAsync(name) ? Results.NoContent() : Results.NotFound());
```

## Testing

```bash
npm test                                                # TypeScript, 224
cd aspnet/Tide.Asgard.AspNetCore
dotnet run --project Tide.Asgard.Scheduler.Tests        # .NET, 238
```

Both run [`tests/fixtures/schedule-expression.json`](../tests/fixtures/schedule-expression.json),
a frozen conformance file: expression plus start instant plus the fire times that
must follow, and bad expressions plus the error code and offset both parsers must
report. Codes rather than messages are the contract, because hand written parsers
drift on invalid input first.

Add a database and both grow — 276 and 290 — with the Postgres store, schedule
store, notifier and migrations:

```bash
export SCHEDULER_TEST_DATABASE_URL=postgres://user:pass@localhost:5432/scheduler_test
```

Worker tests run on a fake clock. The three things that cannot — lease renewal,
notifier wake-ups and concurrent claiming — run on the real clock and each ships
with its control, so a test cannot pass while the feature does nothing.

The .NET suite is larger by the ASP.NET wiring tests, which have no TypeScript
counterpart because that is the .NET host's story, not the shared contract.

## Layout

```
src/scheduler/{expression,execution}/            TypeScript, no dependencies
aspnet/.../Tide.Asgard.Scheduler/                .NET, no dependencies
aspnet/.../Tide.Asgard.Scheduler.Postgres/       .NET, needs Npgsql
aspnet/.../Tide.Asgard.Scheduler.AspNetCore/     .NET, needs Microsoft.Extensions.*
sql/migrations/*.sql                             canonical DDL, append only
```

Only two files per runtime are platform specific — `TimeZone.ts` and
`TimeZoneShim.cs`. Everything above them is the same integer arithmetic in both.

## Not built yet

1. **HTTP endpoints for the admin surface.** The `Worker` is injectable and the
   methods exist, so this is a few lines, as above. Routing and authorisation
   belong to the application, so shipping them here would be guessing at both.
2. **Host wiring for TypeScript.** There is no single framework to target the way
   there is on .NET, so the shape would be a guess. The pieces compose in about
   the same number of lines either way.
