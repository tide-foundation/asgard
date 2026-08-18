# Task scheduler

Work out when something should run next, in TypeScript and .NET, from one shared
expression language. No third party dependencies in either runtime.

It comes in two halves:

- **Expression**: text in, next fire time out. Pure functions, no state.
- **Execution**: a store holds jobs, a worker claims them and runs handlers, with
  leases, retries and backoff.

Two stores ship with it. `InMemoryJobStore` for local timers and tests, and
`PostgresJobStore` for anything that has to survive a restart or coordinate
across replicas. You supply a connection, the SDK supplies the schema and every
query.

## Working out when something runs

Two calls. Parse turns text into a spec, `nextFire` turns a spec plus "now" into
the next instant. Parse once and keep the spec, it is immutable and reusable.

### TypeScript

```ts
import { parseSchedule, nextFire } from "asgard-tide";

const spec = parseSchedule("on 03:30 tz=Australia/Sydney");

const next = nextFire(spec, Date.now());  // epoch ms, or null if it never fires again
console.log(next === null ? "never" : new Date(next).toISOString());
```

### .NET

```csharp
using Tide.Asgard.Scheduler.Expression;

var spec = ScheduleParser.Parse("on 03:30 tz=Australia/Sydney");

long? next = ScheduleEvaluator.NextFire(spec, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
Console.WriteLine(next is null
    ? "never"
    : DateTimeOffset.FromUnixTimeMilliseconds(next.Value).ToString("u"));
```

`nextFire` returns the first instant strictly after the one you pass, and null
when the schedule can never fire again.

### Handling bad expressions

Parse errors carry a stable code and a character offset, so you can point at the
problem in a config file.

```ts
try {
    parseSchedule(userInput);
} catch (e) {
    if (e instanceof ScheduleParseError) console.error(`${e.code} at ${e.offset}`);
}
```

```csharp
catch (ScheduleParseException e)
{
    Console.Error.WriteLine($"{e.Code} at {e.Offset}");
}
```

Both runtimes emit the same code for the same input. `on hour=25` is
`E_VALUE_RANGE` at offset 8 in either language.

## Running jobs

A **job definition** ties a name, a payload type and a handler together. Pass the
definition wherever the job is used and the compiler checks the payload for you.

```ts
const reconcileOrks = defineJob({
    name: "reconcile-orks",
    handler: async (payload: { realmId: string }, ctx) => {
        await reconcile(payload.realmId);
    }
});

const worker = await createScheduler({
    store: new PostgresJobStore(pool),
    jobs: [reconcileOrks],
    schedules: [{
        name: "nightly-reconcile",
        expr: "on 03:00 tz=Australia/Sydney",
        job: reconcileOrks,
        payload: { realmId: "tide" }
    }],
    concurrency: 4
});

await worker.enqueue(reconcileOrks, { realmId: "adhoc" });  // checked
await worker.enqueue(reconcileOrks, { realm: "adhoc" });    // compile error
worker.start();
```

```csharp
var reconcileOrks = Job.Define<ReconcilePayload>(
    "reconcile-orks", async (payload, ctx) => await Reconcile(payload.RealmId));

await using var worker = await Worker.CreateAsync(new WorkerOptions
{
    Store = PostgresJobStore.Create(connectionString),
    Jobs = [reconcileOrks],
    Schedules =
    [
        ScheduleDefinition.For(
            "nightly-reconcile", "on 03:00 tz=Australia/Sydney",
            reconcileOrks, new ReconcilePayload("tide"))
    ],
    Concurrency = 4
});

await worker.EnqueueAsync(reconcileOrks, new ReconcilePayload("adhoc"));
worker.Start();
```

`createScheduler` and `Worker.CreateAsync` do one thing the constructor does not:
apply the store's schema when it has one. That is the step most easily forgotten
and the one whose absence fails at the least convenient moment.

Jobs register themselves when enqueued or scheduled, so there is no separate
registration step to keep in sync. `worker.enqueueByName` is the escape hatch for
an admin endpoint that takes a job name off a request rather than having the
definition to hand.

Under the hood a job is still resolved by **name** at execution time, not
captured as a closure, because a durable store holds a name and a payload rather
than a function. That is what lets a run enqueued by one process execute in
another, including across runtimes.

### Payloads

Payloads always travel as JSON, whichever store is in use. The in memory store
round trips them through JSON deliberately, so a job cannot pass in tests and
then meet a different shape the first time it runs against Postgres. A `Date`
arrives as a string, a class instance as a plain object.

In .NET the payload is deserialized into the definition's type for you. In
TypeScript the raw value is passed through unless the definition supplies
`parse`:

```ts
const strict = defineJob({
    name: "strict",
    parse: raw => {
        if (typeof raw?.realmId !== "string") throw new Error("realmId must be a string");
        return raw;
    },
    handler: payload => reconcile(payload.realmId)
});
```

`parse` runs on **dequeue**, not on enqueue. That is the useful side: it catches
a payload written by an older deploy meeting a handler that has since changed
shape. A payload that fails to convert is dead lettered immediately rather than
retried, because no number of attempts will change what is already stored.

### Admin

Schedules live in a store, so they can be inspected and steered at runtime rather
than only in code.

```ts
await worker.listSchedules();          // name, expr, enabled, next fire, last fire
await worker.pauseSchedule("nightly"); // stops materializing, keeps the definition
await worker.resumeSchedule("nightly");
await worker.triggerSchedule("nightly");  // run it now, timetable untouched
await worker.removeSchedule("nightly");

await worker.cancelRun(runId);         // pending or leased, becomes cancelled
await worker.requeueRun(runId);        // dead or cancelled, back to pending
```

.NET has the same surface with `Async` names: `ListSchedulesAsync`,
`PauseScheduleAsync`, `TriggerScheduleAsync`, `CancelRunAsync`, `RequeueRunAsync`.

Three behaviours are worth knowing:

- **Triggering does not move the timetable.** A manual run uses a different
  idempotency key from a materialized occurrence, so the next scheduled fire is
  unaffected, and a paused schedule can still be triggered deliberately.
- **Re-registering preserves a pause.** `addSchedule` at startup updates the
  definition but leaves `enabled` alone, so a redeploy cannot silently resume
  something an operator stopped. It also keeps the schedule's place in time
  unless the expression itself changed.
- **Cancelling is recorded, not erased.** A cancelled run keeps its row, so the
  decision stays visible. `requeue` gives a dead or cancelled run a fresh set of
  attempts.

Both return `false` rather than throwing when there is nothing to act on, which
is the honest answer to pausing a schedule that does not exist or cancelling a
run that already finished.

### Making schedules durable

By default schedules live in memory, which is right when they are declared in
code and re-registered at startup. Runs they materialize are already durable, and
the idempotency key stops two replicas duplicating an occurrence, so this is not
a correctness gap.

Give the worker a `PostgresScheduleStore` and a pause survives a restart, and a
schedule can be added without a deploy:

```ts
const worker = await createScheduler({
    store: new PostgresJobStore(pool),
    scheduleStore: new PostgresScheduleStore(pool),
    jobs: [reconcileOrks]
});
```

Both tables come from the same migrations, so one `ensureSchema` covers them.

### Making it durable

Swap the store. Nothing else changes: the same worker, handlers and schedules now
survive restarts and coordinate across replicas.

```ts
import { Pool } from "pg";

const store = new PostgresJobStore(new Pool({ connectionString }));
await store.ensureSchema();
```

```csharp
await using var store = PostgresJobStore.Create(connectionString);
await store.EnsureSchemaAsync();
```

`ensureSchema` runs the migrations in [`sql/migrations/`](../sql/migrations/) and
returns the versions it applied. It is safe on every startup and safe from
several processes at once, so there is no migration step to run by hand.

### Migrations

The schema is versioned. `asgard_schema_migrations` records what a database has
seen, and `ensureSchema` applies whatever is missing, in order.

```ts
await migrate(pool);            // returns e.g. [2, 3], or [] when up to date
await appliedMigrations(pool);  // [1, 2, 3]
```

Three rules keep this safe, and each has a test:

- **Migrations are append only.** Never edit one that has shipped. Add the next
  number instead. The suites assert that the embedded copies match
  `sql/migrations/*.sql`, that versions run 1..n with no gaps, and that a file's
  number matches the version it declares.
- **Every migration is safe to apply twice.** A crash between applying one and
  recording it must not leave the schema unrepeatable, so each uses
  `create ... if not exists` or a guarded `alter`.
- **Concurrent migrators are serialized** by a Postgres advisory lock, held for
  the transaction that applies a migration and for the one that bootstraps the
  migrations table itself. That second one matters: `create table if not exists`
  is not atomic against another session doing the same thing, and two processes
  starting together will otherwise collide in the system catalog.

A caller that loses the race is told it applied nothing, rather than claiming
work another process did, because the signal is the migrations insert's own
`returning` rather than a before-and-after comparison.

If you would rather apply the DDL yourself, the files are the whole of it, and
running them in order is equivalent.

The store takes a connection, not a driver choice. In .NET,
`PostgresJobStore.Create` owns an `NpgsqlDataSource` for you, or pass one the
application already has and keep ownership. In TypeScript it accepts anything
with a `query` method, which is what a node-postgres `Pool` or `Client` already
is, so the package itself depends on nothing.

One behaviour changes once jobs are durable: **claiming becomes genuinely
concurrent**. `SELECT ... FOR UPDATE SKIP LOCKED` means workers step over rows
their peers hold rather than blocking, so adding replicas adds throughput instead
of contention.

Payloads do not change, because the in memory store already normalises them the
same way. That is the point of doing so.

### Cutting the latency

By default a worker finds new work by polling, so a job enqueued just after a
poll waits most of an interval. That is fine to thousands of jobs a second, but
it shows on work you want to happen now. Give the worker a notifier and it gets
woken instead:

```ts
import { Client } from "pg";

// LISTEN needs its own session, so this is a Client rather than a Pool.
const listener = new Client({ connectionString });
await listener.connect();

const worker = await createScheduler({
    store: new PostgresJobStore(pool),
    notifier: new PostgresNotifier(pool, listener),
    jobs: [reconcileOrks]
});
```

```csharp
await using var notifier = new PostgresNotifier(dataSource);

await using var worker = await Worker.CreateAsync(new WorkerOptions
{
    Store = new PostgresJobStore(dataSource),
    Notifier = notifier,
    Jobs = [reconcileOrks]
});
```

`NOTIFY` goes out through the pool when a worker enqueues something already due,
including occurrences it materializes. Every worker listening on the channel
wakes, ticks, and whoever wins the claim runs it.

**Polling stays the floor.** A missed notification, a dropped connection, or a
notifier that throws costs latency and never correctness, which is what makes
this safe to add to a running system. There is a test for each: the same
scenario with a broken notifier still runs the job, and reports the failure.

`InMemoryNotifier` does the same thing within one process, which is what to use
alongside `InMemoryJobStore`.

### What a tick does

`start` loops over `tick`, and `tick` is public so tests can drive a worker with
a fake clock instead of racing real timers.

```
reap        expired leases return to pending
purge       settled runs past the retention cutoff are deleted
materialize schedules that have come due become runs
claim       take up to concurrency due runs, under lease
dispatch    run each handler
settle      succeeded, retried with backoff, or dead
```

`start` additionally runs a lease renewal loop beside this one, and waits on the
notifier rather than the clock between passes when one is configured.

### Retries

A failed attempt returns the run to pending with a later time, so `attempt`
survives across attempts rather than starting over.

```
delay = min(baseMs * multiplier^(attempt-1), capMs), then jitter
```

Jitter is `none`, `full` (anywhere in `[0, delay]`) or `equal`
(`[delay/2, delay]`). The default is full, because a fleet that failed together
otherwise retries together. The cap is applied before jitter so it stays an
upper bound.

A handler that throws `PermanentJobError` (`PermanentJobException` in .NET) skips
its remaining attempts and goes straight to dead. Use it for work that cannot
succeed however many times it runs, like a malformed payload.

A recurring schedule survives a run that dies. The successor is enqueued in the
same call that settles the failure, so one bad night does not stop the job.

### Missed occurrences

If the process was down, `misfire` decides what happens.

| Policy | Behaviour |
|---|---|
| `fire_once` | Catch up with a single run. The default: after an outage you usually want the job to happen, once, not sixty times. |
| `fire_all` | Enqueue every missed occurrence. |
| `skip` | Abandon what was missed and wait for the next one. |

### Leases and the execution contract

A claimed run is leased for `leaseMs`. If the worker dies, the reaper returns the
run to pending once the lease expires, which is what makes a crash recoverable.
It is also what makes double execution possible.

> The contract is **at-least-once**. A worker can die after a side effect and
> before its settle call, so handlers must be idempotent. No store fixes this. It
> is a property of running work outside the transaction that records it.

`start` renews the leases of in flight runs for you, every `heartbeatMs`, which
defaults to a third of the lease so two renewals can be missed before one lapses.
Renewal runs alongside the tick loop rather than inside it, because a tick is
blocked on the very handlers that need renewing.

`ctx.heartbeat()` is still there for a handler that wants to check in explicitly.
It returns false when the lease is already gone, at which point the handler
should stop, because another worker has taken the run.

A worker driven by bare `tick` calls does not renew. That is deliberate, and
there is a test for it.

### Mixed fleets

By default a worker claims only runs whose handler it has registered, so in a
fleet where different processes run different jobs, a run waits for a process
that can actually execute it instead of being claimed and failed by one that
cannot.

Set `claimOnlyRegisteredHandlers` to false to have a worker claim everything and
dead letter what it cannot run. That is the right choice when one process owns
every handler and an unknown name means a bug rather than a routing question.

### Retention

Settled runs accumulate forever unless something deletes them. The partial
indexes keep the hot paths fast regardless, but the table still grows.

```ts
new Worker({ store, registry, retention: { afterMs: 7 * 86_400_000, everyMs: 3_600_000 } });
```

Succeeded runs only, unless `includeDead` is set. A dead run is evidence that
something never ran, and deleting it loses the only record of that. Sweeps are
batched, so a long backlog clears over several passes rather than one statement
locking the table.

Set `afterMs` comfortably longer than the period of anything you schedule.
Purging a run drops its idempotency key with it, so a schedule that later
re-materializes the same occurrence would be free to enqueue it again.

### Metrics and per-run visibility

`stats` gives you the totals:

```ts
const { pending, leased, succeeded, dead, cancelled, oldestPendingAgeMs } = await worker.stats();
```

Alert on `oldestPendingAgeMs`. Queue depth lies, because a large batch looks
exactly like an outage, whereas a rising oldest age only ever means work is not
being picked up.

For per-run visibility, give the worker an observer. Every method is optional, so
implement only what you need:

```ts
const worker = await createScheduler({
    store,
    jobs: [reconcileOrks],
    observer: {
        runStarted: ({ run }) => log.info({ runId: run.id, job: run.handler }, "run started"),
        runFinished: ({ run, outcome, durationMs, error, nextAttemptAtMs }) =>
            log.info({ runId: run.id, outcome, durationMs, err: error, nextAttemptAtMs }, "run finished"),
        tickFinished: ({ result, durationMs }) => metrics.observe(result, durationMs)
    }
});
```

```csharp
public sealed class LoggingObserver(ILogger logger) : IJobObserver
{
    // The other methods keep their no-op defaults.
    public void RunFinished(RunFinishedEvent e) =>
        logger.LogInformation("{Job} {Outcome} in {Duration}ms", e.Run.Handler, e.Outcome, e.DurationMs);
}
```

Two deliberate choices here:

- **Callbacks are synchronous.** Awaiting an observer would put your logging or
  tracing on the critical path of every run, where a slow one quietly becomes a
  throughput problem. Queue anything expensive inside the observer.
- **A callback that throws cannot break the run.** It is reported through
  `onError` and otherwise ignored, and there is a test asserting that a worker
  with an observer throwing from every method still runs, settles and reports
  its job.

`runFinished` carries the outcome (`succeeded`, `retried` or `dead`), how long
the handler took, the error when there was one, and when the next attempt is due
for a retry. Correlate `runStarted` and `runFinished` on `run.id` to wrap a trace
span around a run.

## Storing schedules

Store the canonical spec, not the expression text. Specs are never re-parsed at
fire time, so a later change to the language cannot reinterpret a schedule that
is already running. Keep the original text alongside it for display.

```ts
const stored = specToString(parseSchedule("on 03:00 tz=Australia/Sydney"));
const spec = specFromString(stored);
```

```csharp
var stored = ScheduleSpecJson.ToJson(ScheduleParser.Parse("on 03:00 tz=Australia/Sydney"));
var spec = ScheduleSpecJson.FromJson(stored);
```

The JSON carries a version, unrestricted fields collapse to `"any"`, and the
timezone is validated on load rather than at fire time so a zone this host does
not know fails where someone is watching.

## Recipes

Most schedules are one line from this table.

| You want | Write |
|---|---|
| Every day at 3am | `on 03:00` |
| Twice a day | `on hour=3,15` |
| Every 15 minutes | `on minute=*/15` |
| Every 5 minutes, offset by 2 | `on minute=2/5` |
| Weekdays at 9:30am | `on 09:30 dow=mon-fri` |
| Mondays, Wednesdays, Fridays | `on 09:30 dow=mon,wed,fri` |
| Hourly during business hours | `on hour=9-17 minute=0 dow=mon-fri` |
| First of the month | `on day=1 00:15` |
| Last day of the month | `on day=last 23:55` |
| Second Tuesday | `on nth=2 dow=tue 10:00` |
| Last Friday | `on nth=last dow=fri 10:00` |
| Quarterly | `on month=1,4,7,10 day=1` |
| In someone else's timezone | `on 03:00 tz=Australia/Sydney` |
| Five minutes after each run finishes | `every 5m` |
| On a five minute grid regardless of runtime | `every 5m from 2026-01-01T00:00:00Z` |
| Once, at a fixed moment | `at 2026-09-01T03:00:00Z` |

## Expression language

Three trigger kinds, told apart by the leading keyword.

```
on    [HH:MM] [<field>=<value> ...]     calendar
every <duration> [modifiers]            interval
at    <iso-instant>                     one shot
```

### Calendar

A bare `HH:MM` or `HH:MM:SS` is shorthand for the hour and minute fields, since
a daily time is the most common schedule by far. `on 09:30` and
`on hour=9 minute=30` parse to exactly the same spec, and the shorthand mixes
freely with named fields.

| Field | Range | Notes |
|---|---|---|
| `second` | 0-59 | |
| `minute` | 0-59 | |
| `hour` | 0-23 | |
| `day` | 1-31 | or `last` for the final day of the month |
| `dow` | 0-6 or `sun`..`sat` | Sunday is 0 |
| `month` | 1-12 or `jan`..`dec` | |
| `nth` | 1-5 or `last` | which occurrence of `dow` within the month |
| `tz` | IANA zone id | defaults to `UTC` |
| `dstgap` | `fire_at_gap_end`, `skip` | defaults to `fire_at_gap_end` |
| `dstfold` | `fire_first`, `fire_last` | defaults to `fire_first` |

Values accept `*`, `a`, `a,b,c`, `a-b`, `*/n`, `a-b/n` and `a/n`. A bare value
with a step runs from that value to the field maximum, so `minute=10/15` gives
10, 25, 40 and 55.

**The defaulting rule.** Fields finer than the finest one you named collapse to
their floor. Coarser fields stay unrestricted. So `on hour=3` is 03:00:00 daily
rather than every second during hour 3, and `on month=7` is 1 July at midnight.

**Two combinations are rejected rather than guessed at.** Restricting both `day`
and `dow` gives `E_DAY_AMBIGUOUS`, because cron ORs them and that surprises
people. `nth` without `dow` gives `E_NTH_WITHOUT_DOW`.

### Interval and one shot

```
every 30s                                  fixed delay, measured from completion
every 1h30m                                compound duration literal
every 15m from 2026-01-01T00:00:00Z        fixed rate, snapped to a grid
every 1h jitter 30s                        spread a fleet across the period
at 2026-09-01T03:00:00Z                    one shot
```

Duration units are `ms`, `s`, `m`, `h`, `d`. Each may appear once and they must
descend in size, so `1h30m` parses and `30m1h` does not.

`fixed_delay` is the default and measures from the end of the previous run, so
runs never overlap. Pass the completion instant to `nextFire`. `fixed_rate` puts
fires on a grid anchored to `from` regardless of how long a run took, and passing
`from` implies it.

Jitter is stored on the spec but not applied by `nextFire`, which stays
deterministic. Apply it when you enqueue.

## Calendar edge cases

Impossible dates are skipped rather than clamped or thrown, so nothing silently
fires on the wrong day.

| Expression | Behaviour |
|---|---|
| `on day=31` | Only months with 31 days. February, April, June, September and November are skipped. |
| `on day=29 month=2` | Leap days only. From 2026 that is 2028, 2032, 2036. |
| `on day=last` | The real final day, so 28, 29, 30 or 31 as the month requires. |
| `on day=last month=2` | 29 February in leap years, 28 February otherwise. |
| `on nth=5 dow=fri` | Only months that actually have a fifth Friday. |
| `on day=30 month=2` | Never fires. `nextFire` returns null. |

**Leap years are handled by the calendar, not by special cases.** The evaluator
searches wall clock time and asks the platform how long each month is, so the
Gregorian rules including the century exceptions come for free. 2100 is not a
leap year, which puts an 8 year gap between the leap days of 2096 and 2104. The
search horizon is a full 400 year Gregorian cycle rather than a guess, because
the calendar including weekday alignment repeats exactly on that period, so
anything that has not fired within one cycle never will.

Cost is a few hundred microseconds for an ordinary schedule. Proving that a
schedule never fires walks the whole cycle and takes a few milliseconds, which is
a one time answer per schedule.

## Daylight saving

Mapping a wall clock to an instant is not a function. There are three cases, and
both runtimes must agree on all of them or two workers will disagree twice a year.

| Case | Example | Policy | Default |
|---|---|---|---|
| Normal | one matching instant | | |
| Gap, spring forward | 02:30 does not exist | `dstgap` | `fire_at_gap_end` |
| Fold, fall back | 02:30 happens twice | `dstfold` | `fire_first` |

`fire_at_gap_end` fires at the transition instant, the moment the clock resumes.
`skip` drops that occurrence and looks for the next one.

Timezone data comes from the platform, `TimeZoneInfo` on .NET and
`Intl.DateTimeFormat` on Node. No tzdata is bundled.

## Examples

Runnable tours of the API, one per language, producing the same output.

```bash
npm run build:cjs && node examples/typescript/scheduler.js
dotnet run --project examples/dotnet/SchedulerExample
```

## Tests

```bash
npm test                                              # TypeScript, 224 tests

cd aspnet/Tide.Asgard.AspNetCore
dotnet run --project Tide.Asgard.Scheduler.Tests      # .NET, 224 tests
```

The Postgres tests need a real database, because the two properties that matter,
`SKIP LOCKED` and single statement atomicity, cannot be faked. Point them at a
throwaway database and both suites grow to 276:

```bash
export SCHEDULER_TEST_DATABASE_URL=postgres://user:pass@localhost:5432/scheduler_test
npm test
dotnet run --project Tide.Asgard.Scheduler.Tests
```

Without that variable they are skipped, so the default suite needs no database.
The sharpest of them enqueues 200 runs, claims them from 8 concurrent workers,
and asserts every run was claimed exactly once and none appeared twice.

A drift check runs either way: each store embeds a copy of the schema and asserts
they still match `sql/migrations/*.sql`, so the two runtimes and the canonical
files cannot diverge.

Both suites run
[`tests/fixtures/schedule-expression.json`](../tests/fixtures/schedule-expression.json),
a frozen conformance file with two families:

- `sequences`, an expression plus a start instant and the fire times that must
  follow. Covers parsing and evaluation together, including the defaulting rule.
- `errors`, a bad expression plus the code and offset both parsers must report.
  Codes rather than messages are the contract, because hand written parsers
  drift on invalid input first.

On top of that each runtime has unit tests over the parsed spec, over
serialization, and over the store and worker, mirrored case for case so the two
suites stay comparable. Serialization is checked by running every conformance
fixture through storage and asserting the restored spec produces identical fire
times, which covers every expression shape rather than a hand picked few.

Worker tests run on a fake clock, so retries, leases and catch up are exercised
without any waiting. The exception is lease renewal, which is about elapsed time
relative to a lease and so runs on the real clock with short intervals. It comes
with its control: the same scenario driven by bare ticks, asserting the lease
does lapse and another worker does steal the run.

DST fixtures cover both hemispheres, both policies for each case, and
`Australia/Lord_Howe`, whose transition is 30 minutes rather than a whole hour.

.NET and Node read separate copies of the timezone database, so a tzdata skew
between container images is possible. Running both suites in CI turns that into a
fixture failure on a transition case rather than a mystery double fire in
production.

## Layout

```
src/scheduler/                                  TypeScript
  expression/
    Tokenizer.ts  Parser.ts  Spec.ts  FieldSet.ts
    Duration.ts   Evaluator.ts  Serialization.ts
    TimeZone.ts                                 only platform specific file
  execution/
    Clock.ts  RetryPolicy.ts  JobRun.ts  JobStore.ts
    InMemoryJobStore.ts  PostgresJobStore.ts
    ScheduleStore.ts  InMemoryScheduleStore.ts  PostgresScheduleStore.ts
    JobNotifier.ts  PostgresNotifier.ts  JobObserver.ts
    Migrations.ts  MisfirePolicy.ts  JobDefinition.ts
    HandlerRegistry.ts  Worker.ts  Scheduler.ts

aspnet/.../Tide.Asgard.Scheduler/               .NET, no dependencies
  Expression/
    Tokenizer.cs  ScheduleParser.cs  ScheduleSpec.cs  FieldSet.cs
    DurationParser.cs  ScheduleEvaluator.cs  ScheduleSpecJson.cs
    TimeZoneShim.cs                             only platform specific file
  Execution/
    Clock.cs  RetryPolicy.cs  JobRun.cs  IJobStore.cs
    InMemoryJobStore.cs  IScheduleStore.cs  InMemoryScheduleStore.cs
    IJobNotifier.cs  IJobObserver.cs  JobDefinition.cs
    HandlerRegistry.cs  Worker.cs

aspnet/.../Tide.Asgard.Scheduler.Postgres/      .NET, needs Npgsql
  PostgresJobStore.cs  PostgresScheduleStore.cs
  PostgresNotifier.cs  SchedulerMigrations.cs

sql/migrations/*.sql                            canonical DDL, append only
```

Everything outside the two timezone files is integer arithmetic running the same
algorithm in both languages.

The Postgres store is a separate .NET package so the core keeps no dependencies.
TypeScript needs no split, because the store there accepts any object with a
`query` method rather than importing a driver.

## Not built yet

1. **An HTTP surface for the admin methods.** They exist on the worker; exposing
   them is deliberately left to the host, since routing and authorisation belong
   to the application rather than the SDK.
2. **Dependency injection and hosted service wiring for ASP.NET Core.** A
   `services.AddAsgardScheduler(...)` and an `IHostedService` would save every
   .NET host the same dozen lines. It is left out for now because the scheduler
   core deliberately has no dependencies, so it belongs in a separate package
   alongside the Postgres one rather than in the core.
