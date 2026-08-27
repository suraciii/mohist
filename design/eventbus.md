# Event Bus

## Purpose

Aggregate event tables already persist domain events. The system needs one
notifier that delivers persisted events to subscribers with at-least-once
semantics. Do not add a broker, queue, streaming SDK, or per-stream grain.

The previous design fused all dispatch work into one cluster-singleton grain.
That design cannot scale; this revision replaces it with lease-based stream
workers. What does not change: the event tables are the queue, handlers are
idempotent by `EventId`, and delivery is at-least-once.

## Durable Sources

One dispatch query covers undispatched rows from the event tables, one per
durable source aggregate: WorkflowRun, Issue, Epic, AgentSession, AgentJob,
Ingress, and Workspace.

A stream contains rows from one event table with the same `Source`, ordered by
per-source `Id`. Stream ID is Source, such as `/mohist/workflow-runs/{runId}`.
This is not event sourcing. State is stored separately. A stream provides
notification and audit.

Per-stream FIFO order is the only ordering contract. Cross-stream order has
never been a contract and is not one now.

## Subscription Contract

The stable mechanism is `ICloudEventHandler`, `[Subscription]`, and dependency
injection, unchanged.

- **System consumers** are compile-time `[Subscription]` handlers using a type
  glob: exact type, prefix wildcard, `*`, or `a|b|c`.
- **User consumers** are Agent routing rules using the CEL-subset matcher in
  [`event-protocol.md`](event-protocol.md).

Handlers must be idempotent by `EventId`. A retried event re-invokes every
matching handler, including ones that succeeded on an earlier attempt. No
per-handler attempt state exists anywhere; the retry unit is the event.

## Persistence

State and its events save in one EF transaction. Commit makes both durable.
`PublishAsync` only writes one row and never invokes a handler. Dispatcher owns
notification.

Each event table carries one nullable delivery timestamp as its only delivery
marker. Null means undispatched; a timestamp means dispatched. There is no
cursor table and no per-stream offset. The lease table below is worker
coordination state, not an offset; a lost lease never loses or reorders a row.

## Dispatcher

```text diagram
Producer transaction -- append row --> commit -- signal channel (in-proc) --+
                                                                          |
                                                                          v
+---------------------------------------------------------------------------+
| N dispatch workers (in-proc hosted services)                              |
|                                                                           |
| discover streams with undelivered rows (slow poll = correctness path)     |
| claim a stream: write lease row if free or expired   (stream lease table) |
| drain claimed stream in Id order:                                          |
|   run every matching handler once per attempt                              |
|   on failure: attempts++ on lease, park stream until backoff elapses      |
|   at MaxAttempts: dead-letter the head row, mark it dispatched, advance   |
|   settle the contiguous delivered prefix in one transaction               |
| release lease when the stream is empty                                     |
+---------------------------------------------------------------------------+
```

### Stream leases

One table, `DispatchStreamLease`, keyed by (Origin, Source), holds
`LeaseOwner`, `LeaseUntil`, `Attempts`, `NextAttemptAt`, and `LastError`.
A lease row exists only while a stream is claimed or parked; an idle stream
has no row. Leases are advisory locks: claiming is an atomic
insert-or-steal on the row. A crashed worker's lease expires by
`LeaseUntil` and another worker steals the stream. Because a lease only
gates who drains, never what is durable, expiry costs at-least-once
redelivery, exactly like a crashed cycle today.

### Ordering and parking

One worker owns one stream at a time and delivers rows in Id order, so
per-stream FIFO holds by construction. A failing head row parks its stream:
`Attempts` and `NextAttemptAt` on the lease delay the next attempt, and the
stream stops occupying worker attention until then. Other streams advance
independently — parked streams cannot starve them, because discovery skips
streams whose lease is parked and batch capacity is never consumed by
skipped rows. The blocked-sources gauge now counts parked leases, which is
durable and exact, instead of a per-cycle in-memory snapshot.

### Wake-up

After an event transaction commits, the producer writes to an in-process
signal channel; idle workers wake immediately. The signal is best-effort: a
lost signal costs at most one slow-poll interval. The slow poll is the
correctness path, replacing the Orleans reminder. There is no grain call in
the wake-up path and no cluster-singleton activation.

An explicit `DrainAsync` (operator nudge, test pump) is also an in-proc
barrier: while another local claim is in flight the drain waits and
re-checks instead of returning early, so it returns only when this process
has settled everything it can. Claims held by other processes are not
waited on — lease expiry covers them.

### Backoff and dead-letter

Exponential backoff `BaseBackoff * 2^(attempt-1)`, capped at `MaxBackoff`,
attempts counted per stream head and persisted on the lease. At
`MaxAttempts` the head row is written to the dead-letter queue through
`IDeadLetterStore` with the last failure, marked dispatched, and the stream
advances with attempts reset. `Attempts` survives restarts; a parked stream
recovers with its retry budget intact.

### Settlement

Delivered rows are marked in one transaction per contiguous prefix per
drain, not one round-trip per row. A settlement failure retries the same
prefix on the next attempt without re-running handlers that already
succeeded in the current drain pass.

## Removed

The singleton grain design and everything it needed:

- `IEventDispatcherGrain`, `EventDispatcherGrain`, its reminder, and
  `DispatcherActivationService`.
- The in-memory handler-state table and its restart-amnesia.
- The poke round-trip and the `PokeAsync` coalescing from #702; the signal
  channel supersedes both.
- Per-handler attempt independence; the retry unit is the event.
- `DispatchNowAsync` as a grain call. The drain entry point is an in-proc
  interface used by tests and the operator redelivery route.

## External delivery

Subscription handlers that call external systems (webhook fan-out, GitHub
write-back) run inline in workers today. A worker occupied by slow external
I/O delays only the streams it owns. The proven extraction pattern is the
Slack outbox: a handler writes a delivery outbox row in the drain, and a
separate dispatcher owns the external call. Webhook and GitHub handlers
should move to that pattern when their latency shows up in dispatch lag;
that extraction is deliberately not part of this change.

## Error Ladder

- **Eliminate:** Persist event and state in one transaction.
- **Absorb:** Lease-level exponential backoff, durable across restarts.
- **Aggregate:** Workers catch at the boundary. A handler must not swallow
  an exception.
- **Expose:** Exhausted delivery lands in the queryable, retryable DLQ;
  parked streams are visible as durable lease rows and one gauge.
