---
status: converged
---

# Event Bus

## Purpose

Aggregate event tables already persist domain events. The system needs one
notifier that delivers persisted events to subscribers with at-least-once
semantics. Do not add a broker, queue, streaming SDK, or per-stream grain. Use
one self-driven dispatcher.

## Durable Sources

One dispatch cycle queries undispatched rows from five event tables. These five
aggregates are the durable sources for this bus.

| Aggregate | Events | On bus |
|---|---|---|
| WorkflowRun | State transitions, Completed, Failed | Yes |
| Issue | work-started, work-completed, closed | Yes |
| Epic | State transitions and automatic revival | Yes |
| AgentSession | Runtime bound and state changes | Yes |
| AgentJob | Failed | Yes |

These sources cover all current cross-aggregate processes advanced by the event
bus. Other domains such as Runner and Project either produce no dispatchable
domain event or place required context on an event from these aggregates.
Session is a leaf tracking domain to which no other domain reacts. AgentSession
rows enter the bus only for dispatch and do not imply a business subscriber.

## Subscription Contract

The stable mechanism is `ICloudEventHandler`, `[Subscription]`, and dependency
injection.

Two consumer types match the same envelope through mechanisms suited to their
surface:

- **System consumers** are compile-time `[Subscription]` handlers using a type
  glob. This is a subset of expression behavior equivalent to exact event type
  or prefix matching. Code handlers do not need runtime expressions.
- **User consumers** are Agent routing rules using the CEL-subset matcher in
  [`event-protocol.md`](event-protocol.md). They match the complete envelope,
  including `type`, `source`, and every context extension attribute.

Any event routable to a system handler must also be subscribable through a user
expression.

`[Subscription]` type matching supports:

| Pattern | Match |
|---|---|
| `com.mohist.workflow.run.completed` | Exact |
| `com.mohist.workflow.*` | Prefix wildcard |
| `*` | All |
| `a\|b\|c` | Any listed value |
| `foo.*.bar` | Rejected |

## Persistence

Each event table has one nullable `DispatchedAt` column as its only delivery
marker.

- `NULL` means undispatched.
- A timestamp means dispatched.
- There is no cursor table or per-stream offset.

State and its events save in one EF transaction. Commit makes both durable.
`PublishAsync` only writes one row and never invokes a handler. Dispatcher owns
notification.

## Stream

A stream contains rows from one event table with the same `Source`, ordered by
per-source `Id`.

- Stream ID is Source, such as `/mohist/workflow-runs/{runId}`.
- This is not event sourcing. State is stored separately. A stream provides
  notification and audit.
- Cross-stream work is event to command, such as
  `WorkflowRunCompleted -> CompleteIssue`.

## Dispatcher

```text
Producer transaction -- append row --> commit -- poke after commit --+
                                                                  |
                                                                  v
+-----------------------------------------------------------------------+
| IEventDispatcherGrain, cluster singleton                              |
|                                                                       |
| Orleans reminder tick                                                 |
|   query undispatched rows ordered by Source and Id                    |
|   fan out each row to matching handlers                               |
|   apply EventDispatcherOptions exponential backoff                    |
|   after excess attempts, write DLQ and mark DispatchedAt              |
+-----------------------------------------------------------------------+
```

### Wake-up

After an event transaction commits or a failure event appends successfully,
each durable producer, WorkflowRun, Issue, Epic, AgentSession, and AgentJob,
calls `EventDispatcherPoke.PokeAfterCommit` to send a fire-and-forget wake-up to
`IEventDispatcherGrain`. The helper absorbs poke failures such as unavailable
grain or serialization failure and writes a debug log. Producer command result
and persisted event remain unchanged. A command that produces no event, such as
an idempotent Epic operation, does not poke.

The **reminder is the correctness path**. A poke only advances the next cycle
from at most `ReminderPeriod` later to the next dispatcher scheduling yield. A
lost poke, failed process, or reminder that ticks before the event cannot lose
the row. The reminder later finds it and delivers in FIFO order.

### Backoff

Use custom exponential backoff, with no third-party retry or resilience
library. Each handler matched to one event has independent attempt count and
next retry time. Compute delay as
`EventDispatcherOptions.BaseBackoff * 2^(attempt-1)`, capped by
`EventDispatcherOptions.MaxBackoff`. At
`EventDispatcherOptions.MaxAttempts`, stop retries, write the dead letter to
`IDeadLetterStore`, and mark the event dispatched. Attempt count and next retry
time are not persisted.

Failure of one handler does not affect another handler's attempt count. Each
handler for one row advances independently.

### Settlement Retention

Dispatcher keeps an in-process table of settled handler state, `Completed` or
`DeadLettered`, for each event key. It removes an event key only after
`IEventStore.MarkDispatchedAsync` or `IDeadLetterStore.SettleAsync` persists.
When a settlement write fails, the next cycle retries only that write. It does
not rerun handlers or reset attempt counts.

This retention is **in process**. A dispatcher restart clears the table. The
reminder supplies an undispatched row again and handler attempts restart from
zero. Persistent retry state is outside current scope.

### FIFO Blocking and Visibility

Within a dispatch cycle, deliver rows for one Source in `(Source, Id)` order.
If a row has a handler waiting for its next retry time, skip later rows for that
Source during the cycle to preserve FIFO. Other Sources advance independently.

After each cycle, dispatcher writes the number of blocked Sources to an
attribute-free ObservableGauge with unit `1`:
`mohist.server.event_dispatcher.blocked_sources`. Source identifiers, event IDs,
and attempts must never be metric attributes because they create high
cardinality. The Gauge reports the most recently completed cycle and is zero
during a cycle or immediately after dispatcher startup. This is the only
current operations signal for blocking.

### Crash and Recovery

- After a crash during any cycle, a row without `DispatchedAt` remains. The
  reminder redelivers it. Every handler must be idempotent by `EventId`.
- A DLQ row receives `DispatchedAt` immediately. It remains queryable and can be
  redelivered manually through `IDeadLetterStore.StartRedeliveryAsync`,
  `ResolveAsync`, and `RecordRedeliveryFailureAsync`. HTTP contracts remain
  unchanged. This design adds no DLQ query or redelivery API.

## Error Ladder

- **Eliminate:** Persist event and state in one transaction.
- **Absorb:** Apply custom exponential backoff through
  `EventDispatcherOptions.BaseBackoff`, `MaxBackoff`, and `MaxAttempts`.
- **Aggregate:** Dispatcher catches at the boundary. A handler must not swallow
  an exception.
- **Expose:** Put exhausted delivery in a queryable and retryable DLQ.
