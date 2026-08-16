# Design: Runtime Event Outbox Delivery Lease

## Decision

The runner keeps a process-local lease keyed by
`runtimeEventSchedulingKey(record)`. A lease holds the immutable batch sent to
the transport. Durable outbox state remains the sole authority for replay;
the lease is only a local fence against concurrent sends while the original
request could still reach the Server.

## State Model

For one scheduling group:

```text
ready -> sending -> acknowledged -> ready/removed
                   -> rejected -> ready (one-shot retry)
                   -> deadline -> quarantined
quarantined -- original promise settles --> acknowledged or rejected
```

`quarantined` means the deadline elapsed, not that the Server rejected the
request. Its lease remains owned until the original promise settles. The
drainer omits quarantined groups, but may drain any ready group. If all
remaining records belong to quarantined groups, no retry timer is scheduled.
This is event-driven isolation, not polling.

## Receipt and Persistence Boundary

The late completion is fed into the same receipt matcher used by an on-time
completion. It removes only records whose current map entry is the original
batch entry, then uses the existing serialized atomic snapshot write. The
lease is released after that settlement path completes.

A non-matching receipt, a transport error, or snapshot failure retains the
record. Once the lease is released, the pre-existing retry behavior may make a
new request. It never runs concurrently with the original request. No code
creates a replacement receipt, changes an input delivery id, or treats an
empty response as a matching receipt when the existing policy does not.

At the deadline, an input waiter receives the local timeout so its current
action cannot start an unconfirmed runtime turn. A matching late response is
still retained under the original delivery id, but it does not reactivate the
action that already failed its input boundary.

## Stop Boundary

`stop()` aborts active controllers and clears process-local leases. A late
completion after stop does not mutate or delete durable records and does not
kick a new drain. On a future process start, the unchanged snapshot follows
the existing at-least-once recovery behavior.

## Verification

Focused runner coverage uses a fake `sendBatch` that never resolves and
ignores its abort signal. It proves that:

1. the timed-out group remains durable and is not sent again while its original
   promise is pending;
2. an unrelated scheduling group is eventually sent through the existing
   retry trigger;
3. a matching late receipt removes the original record once and retains its
   original receipt identity.
