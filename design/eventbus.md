---
status: converged
---

# Event Bus

## What it does

Domain events are already persisted (per-aggregate event tables). The system needs a notifier: deliver persisted events to subscribers at-least-once. No broker, no queue, no streaming SDK, no per-stream grain. Just a self-driving dispatcher.

## What goes in

| Aggregate | Events | In bus? |
|---|---|---|
| WorkflowRun | state transitions, Completed, Failed | yes |
| Issue | work-started, work-completed, closed | yes |
| Runner | Disconnected (open) | yes |
| Session | — | no |

Session is a leaf trace domain. No domain reacts to it.

## Subscription contract

`ICloudEventHandler` + `[Subscription]` + DI. Stable mechanism, unchanged.

`[Subscription]` type syntax:

| Pattern | Match |
|---|---|
| `com.mohist.workflow.run.completed` | exact |
| `com.mohist.workflow.*` | prefix wildcard |
| `*` | all |
| `a\|b\|c` | any of |
| `foo.*.bar` | forbidden |

## Persistence

Each event table gets one column: `DispatchedAt` (nullable). That is the only delivery marker.

- `NULL` = undelivered.
- `timestamp` = delivered.
- No cursor table. No per-stream offset.

Events write in the same EF transaction as state save. Commit = persisted.
`PublishAsync` writes a row. Never fires handlers. Notification is the dispatcher's job.

## Streams

A stream = all rows in an event table with the same `Source`, ordered by per-source `Id`.

- Stream id = Source (e.g. `/mohist/workflow-runs/{runId}`).
- Not event-sourced. State stored separately. Stream = notify + audit.
- Cross-stream: event → command. `WorkflowRunCompleted` → `CompleteIssue`.

## Dispatcher

```
Transaction ──write row──▶ commit        ← producers append only
        │
   Dispatcher (cluster singleton)
   ┌──────────────────────────────┐
   │ Orleans reminder ~1s tick     │
   │   query undispatched rows     │
   │   per row: fanout to handlers │
   │   Polly retry; dead → DLQ   │
   │   UPDATE DispatchedAt = now   │
   └──────────────────────────────┘
```

- Producers append. Never wake the dispatcher.
- Dispatcher = Orleans named grain + persistent reminder. Self-healing.
- Single query per tick. Cost = undispatched row count, not stream count.
- Per-stream FIFO: serial dispatch, ordered by (Source, Id).
- Crash: row still NULL → redeliver. Handler must be idempotent on EventId.
- Poison: DLQ + set DispatchedAt. Queryable, manually retryable.

Future: N dispatchers keyed by `hash(Source) % N`. Same source → same grain. Not yet.

## Error ladder

- Eliminate: event + state in same transaction.
- Absorb: Polly exponential backoff.
- Aggregate: dispatcher catches all. Handlers never swallow.
- Surface: DLQ, queryable, retryable.
