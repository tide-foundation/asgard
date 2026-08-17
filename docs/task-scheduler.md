# Task scheduler

A durable job scheduler with matching TypeScript and .NET implementations that
speak one shared contract. Neither implementation takes a third party
dependency. The timezone database comes from the platform: `TimeZoneInfo` in
.NET, `Intl.DateTimeFormat` in TypeScript.

This document covers the expression subsystem, which is the part that is built.
The store and worker layers are listed under [Not yet built](#not-yet-built).

## Expression language

Three trigger kinds, distinguished by their leading keyword so the parser needs
one token of lookahead.

```
on    <field>=<value> ...            calendar
every <duration> [modifiers]         interval
at    <iso-instant>                  one shot
```

### Calendar

Fields are named rather than positional, so they are order independent and
self documenting.

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

```
on hour=3                                  daily at 03:00:00
on minute=*/5                              every five minutes on the grid
on hour=9-17 minute=0 dow=mon-fri          hourly through the working day
on dow=mon,wed,fri hour=9 minute=30 tz=Australia/Sydney
on day=1 hour=0 minute=15                  monthly
on day=last hour=23 minute=55              final day of every month
on nth=2 dow=tue hour=10                   second Tuesday
on nth=last dow=fri hour=10                final Friday
on month=1,7 day=1                         1 January and 1 July at midnight
```

### The defaulting rule

Cron's implicit defaulting is the field most people get wrong. It is explicit
here:

> Fields finer than the finest one you named default to their floor. Fields
> coarser than it stay unrestricted.

So `on hour=3` is 03:00:00 daily, not "every second during hour 3", and
`on month=7` is 1 July at midnight. The parser applies this once and the stored
spec has every field populated, so the evaluator contains no defaulting logic.

### Rejected rather than ambiguous

Standard cron ORs `day` and `dow` when both are restricted. That combination is
rejected here with `E_DAY_AMBIGUOUS` instead of inheriting the surprise.
`nth` without `dow` is rejected with `E_NTH_WITHOUT_DOW`.

### Interval and one shot

```
every 30s                                  fixed delay, measured from completion
every 1h30m                                compound duration literal
every 15m from 2026-01-01T00:00:00Z        fixed rate, snapped to a grid
every 1h jitter 30s                        spread a fleet across the period
at 2026-09-01T03:00:00Z                    one shot
```

Duration units are `ms`, `s`, `m`, `h`, `d`. They may appear at most once each
and must descend in size, so `1h30m` parses and `30m1h` does not.

`fixed_delay` measures the next fire from the completion of the previous run, so
runs can never overlap. It is the default and is usually what "rerun this
function every five minutes" means. `fixed_rate` puts fires on a grid anchored
to `from`, regardless of how long a run took. Passing `from` implies
`fixed_rate`.

Jitter is stored on the spec but deliberately not applied by the evaluator, so
that `nextFire` stays deterministic and can be pinned by fixtures. The scheduler
applies it at enqueue time.

## Daylight saving

Mapping a wall clock to an instant is not a function. There are three cases and
both runtimes must agree on all of them, or two workers will disagree twice a
year.

| Case | Example | Policy | Default |
|---|---|---|---|
| Normal | one matching instant | | |
| Gap, spring forward | 02:30 does not exist | `dstgap` | `fire_at_gap_end` |
| Fold, fall back | 02:30 happens twice | `dstfold` | `fire_first` |

`fire_at_gap_end` fires at the transition instant, which is the moment the clock
resumes. `skip` drops the occurrence and looks for the next one.

## Cross runtime contract

The two implementations are checked against
[`tests/fixtures/schedule-expression.json`](../tests/fixtures/schedule-expression.json),
a frozen fixture file with two families:

- `sequences`, an expression plus a starting instant and the fire times that
  must follow. This validates parsing and evaluation together, including the
  defaulting rule.
- `errors`, a bad expression plus the error code and character offset both
  parsers must report. Error codes rather than messages are the contract,
  because hand written parsers drift on invalid input first.

DST fixtures cover a southern and a northern hemisphere zone, both policies for
each case, and `Australia/Lord_Howe`, whose transition is 30 minutes rather than
a whole hour.

```bash
npm test                                                 # TypeScript
cd aspnet/Tide.Asgard.AspNetCore
dotnet run --project Tide.Asgard.Scheduler.FixtureRunner # .NET
```

.NET and Node read separate copies of the timezone database, so a tzdata version
skew between container images is possible. Running both suites in CI turns that
into a fixture failure on a transition case rather than a mystery double fire in
production.

## Layout

```
src/scheduler/expression/            TypeScript
  Tokenizer.ts  Parser.ts  Spec.ts  FieldSet.ts
  Duration.ts   Evaluator.ts
  TimeZone.ts                        the only platform specific file

aspnet/.../Tide.Asgard.Scheduler/Expression/    .NET
  Tokenizer.cs  ScheduleParser.cs  ScheduleSpec.cs  FieldSet.cs
  DurationParser.cs  ScheduleEvaluator.cs
  TimeZoneShim.cs                    the only platform specific file
```

Everything outside the two timezone files is integer arithmetic and follows the
same algorithm in both languages.

## Not yet built

1. Spec serialization to and from JSON. Schedules store the canonical spec plus
   the original expression text for display, and are never re-parsed at fire
   time, so a later change to the language cannot reinterpret live schedules.
2. `JobStore` interface, in-memory store, handler registry and worker loop.
3. Retry policy with exponential backoff and jitter, and dead lettering.
4. Postgres store: claim and lease with `FOR UPDATE SKIP LOCKED`, heartbeat,
   reaper, and the complete-and-chain transaction that enqueues the next
   occurrence in the same commit that settles the current one.
5. Admin surface: trigger now, pause, cancel, requeue dead.

The execution contract will be at-least-once. A worker can die after a side
effect and before its commit, so handlers must be idempotent.
