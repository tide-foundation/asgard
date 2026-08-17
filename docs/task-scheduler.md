# Task scheduler

Work out when something should run next, in TypeScript and .NET, from one shared
expression language. No third party dependencies in either runtime.

Today this is the expression subsystem: parse a schedule, ask it for the next
fire time. The durable store and worker loop are [not built yet](#not-built-yet),
so you drive it yourself. That is a handful of lines and is shown below.

## Usage

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

### Rerunning a function

```ts
async function runOnSchedule(expr: string, work: () => Promise<void>, signal: AbortSignal) {
    const spec = parseSchedule(expr);
    while (!signal.aborted) {
        const next = nextFire(spec, Date.now());
        if (next === null) return;
        await new Promise(r => setTimeout(r, Math.max(0, next - Date.now())));
        if (signal.aborted) return;
        await work();
    }
}

runOnSchedule("every 5m", syncOrks, controller.signal);
```

```csharp
static async Task RunOnSchedule(string expr, Func<Task> work, CancellationToken ct)
{
    var spec = ScheduleParser.Parse(expr);
    while (!ct.IsCancellationRequested)
    {
        var next = ScheduleEvaluator.NextFire(spec, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (next is null) return;

        var delay = next.Value - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), ct);
        await work();
    }
}
```

This is in-process only. Nothing survives a restart and two replicas will both
fire. Use it for cache refreshes and local timers, not for anything that must
happen exactly once across a fleet.

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
npm test                                              # TypeScript, 90 tests

cd aspnet/Tide.Asgard.AspNetCore
dotnet run --project Tide.Asgard.Scheduler.Tests      # .NET, 90 tests
```

Both suites run
[`tests/fixtures/schedule-expression.json`](../tests/fixtures/schedule-expression.json),
a frozen conformance file with two families:

- `sequences`, an expression plus a start instant and the fire times that must
  follow. Covers parsing and evaluation together, including the defaulting rule.
- `errors`, a bad expression plus the code and offset both parsers must report.
  Codes rather than messages are the contract, because hand written parsers
  drift on invalid input first.

On top of that each runtime has unit tests over the parsed spec, mirrored case
for case so the two suites stay comparable.

DST fixtures cover both hemispheres, both policies for each case, and
`Australia/Lord_Howe`, whose transition is 30 minutes rather than a whole hour.

.NET and Node read separate copies of the timezone database, so a tzdata skew
between container images is possible. Running both suites in CI turns that into a
fixture failure on a transition case rather than a mystery double fire in
production.

## Layout

```
src/scheduler/expression/                       TypeScript
  Tokenizer.ts  Parser.ts  Spec.ts  FieldSet.ts
  Duration.ts   Evaluator.ts
  TimeZone.ts                                   only platform specific file

aspnet/.../Tide.Asgard.Scheduler/Expression/    .NET
  Tokenizer.cs  ScheduleParser.cs  ScheduleSpec.cs  FieldSet.cs
  DurationParser.cs  ScheduleEvaluator.cs
  TimeZoneShim.cs                               only platform specific file
```

Everything outside the two timezone files is integer arithmetic running the same
algorithm in both languages.

## Not built yet

What exists is the expression subsystem only: text in, next fire time out. It has
no state, no storage and no threads. Everything below is still to come.

1. **Spec serialization** to and from JSON. Schedules will store the canonical
   spec plus the original expression text for display, and will never be
   re-parsed at fire time, so a later change to the language cannot reinterpret
   live schedules.
2. **Job store and worker loop.** A `JobStore` interface, an in-memory
   implementation, a handler registry that maps a job name to a function, and the
   loop that claims due work and dispatches it.
3. **Retry.** Exponential backoff with jitter, a cap on attempts, dead
   lettering, and a permanent failure type that skips remaining attempts.
4. **Postgres store.** Claim and lease with `FOR UPDATE SKIP LOCKED`, heartbeat
   for long handlers, a reaper for expired leases, and the complete-and-chain
   transaction that enqueues the next occurrence in the same commit that settles
   the current one.
5. **Admin surface.** Trigger now, pause, cancel, requeue dead.

Until item 2 lands there is no durability and no coordination between processes.
When it does, the execution contract will be at-least-once: a worker can die
after a side effect and before its commit, so handlers must be idempotent.
