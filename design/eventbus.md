# Event Bus

The Event Bus delivers persisted domain events to subscribers with at-least-once
semantics. Event tables are the queue. The design adds no broker, streaming
SDK, or per-stream grain.

## Core Decisions

- State and its event commit in one transaction. A published event is durable
  before delivery begins.
- Handlers are idempotent by `EventId`. The event, not a handler attempt, is the
  retry unit.
- One stream contains one source's rows in per-source `Id` order. FIFO applies
  within a stream only.
- A lease decides which worker drains a stream. It never owns event durability,
  offsets, or ordering.
- A nullable delivery timestamp is the only event delivery marker. There is no
  cursor table, offset, or per-handler attempt state.
- A signal wakes workers quickly. Slow polling remains the correctness path.

## System Boundary

```text diagram
 +------------------+
 | append event row |
 +---------+--------+
           |
           v
      +--------+
      | commit |
      +----+---+
           |
           v
      +--------+
      | signal |
      +----+---+
           |
           v
      +---------+
      | workers |
      +----+----+
           |
           v
     +----------+
     | discover |
     +-----+----+
           |
           v
   +--------------+
   | claim stream |
   +-------+------+
           |
           v
 +-------------------+
 | drain in Id order |
 +---------+---------+
           |
           v
   +---------------+
   | settle prefix |
   +-------+-------+
           |
           v
   +---------------+
   | release lease |
   +---------------+
```

The producer appends the row and state in one EF transaction. `PublishAsync`
only writes the row. Dispatch workers own notification, handler invocation,
lease coordination, and delivery marking. Handlers own their external effects;
they do not own event delivery state.

## Durable Sources

The dispatcher queries undispatched rows from one table per source aggregate:
WorkflowRun, Issue, Epic, AgentSession, AgentJob, Ingress, and Workspace.

A stream contains rows from one event table with the same `Source`, ordered by
per-source `Id`. Its ID is the source identity, such as
`/mohist/workflow-runs/{runId}`. This is notification and audit, not event
sourcing. State remains in its owning aggregate.

## Subscription Contract

The stable system-consumer mechanism is `ICloudEventHandler`, `[Subscription]`,
and dependency injection. A system subscription supports an exact type, a
prefix wildcard, `*`, or an `a|b|c` alternation. User consumers use the
CEL-subset matcher defined in
[`event-protocol.md`](event-protocol.md).

Every handler must be idempotent by `EventId`. A retry invokes every matching
handler again, including handlers that succeeded on an earlier attempt.

## Dispatch Semantics

### Stream Leases

`DispatchStreamLease` is keyed by `(Origin, Source)`. It holds
`LeaseOwner`, `LeaseUntil`, `Attempts`, `NextAttemptAt`, and `LastError`. A row
exists only while a stream is claimed or parked. An idle stream has no lease
row.

Claiming inserts the row or atomically steals it after `LeaseUntil`. A crashed
worker therefore causes at-least-once redelivery. Lease expiry never loses or
reorders a row.

### Ordering and Wake-up

One worker drains one stream at a time in `Id` order. A failing head row parks
the stream until `NextAttemptAt`; other streams continue independently.

After an event transaction commits, the producer signals an in-process channel.
A lost signal costs one slow-poll interval. Slow polling is the correctness
path and does not call an Orleans grain. `DrainAsync` waits for local claims in
flight and rechecks until this process settles everything it can. It does not
wait for claims held by another process.

### Retry and Dead Letter

Backoff is `BaseBackoff * 2^(attempt-1)`, capped at `MaxBackoff`. Attempts are
counted for the stream head and persist on its lease.

At `MaxAttempts`, the dispatcher writes the head row and its last failure to
`IDeadLetterStore`, marks the row dispatched, advances the stream, and resets
attempts. A parked stream resumes after restart with its retry budget intact.

### Settlement

A successful drain marks one contiguous delivered prefix in one transaction.
If settlement fails, the same prefix is retried. Handlers that already
succeeded in that drain pass are not invoked again during that pass.

## Failure Semantics

- **Persist:** state and event commit together.
- **Retry:** lease-level exponential backoff survives restart.
- **Contain:** workers catch failures at the handler boundary; handlers must
  not swallow exceptions.
- **Expose:** exhausted delivery remains queryable in the retryable dead-letter
  store. Parked streams remain visible as lease rows and a blocked-sources
  gauge.
