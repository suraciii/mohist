# Volatile Runtime Event Evidence

Status: accepted

## Problem

The Runner keeps runtime-event delivery records in bounded volatile memory. A `session.input` record can also have a bounded waiter that needs delivery evidence to explain a timeout. The queue record and the waiter have different lifetimes: a delivery can remain in flight after its waiter times out, and a new waiter can start before that old delivery settles.

If attempts and retry reasons belong to the queue record, a late verdict can recreate evidence after its owner ended or can contaminate a newer wait interval. If waiter completion cancels the queue record or delivery lease, a late matching receipt cannot retire the record safely and the Runner can duplicate delivery.

## Decision

Receipt-wait evidence belongs to one active bounded waiter interval, not to the queued record.

Each bounded interval has one queue-local generation. It starts with zero attempts, zero retries, and no latest reason. Coalesced bounded callers share that interval. The interval ends when its waiter resolves, receives a definitive refusal, times out, is cancelled, stops with the queue, or otherwise leaves the existing waiter lifecycle. Ending the interval removes its evidence and invalidates its generation. It does not remove the queue record or cancel the delivery lease.

Each delivery attempt captures the active interval generation when it starts, or captures no generation when no bounded waiter is active. The attempt may change evidence only while its captured generation still equals the active generation. An old attempt cannot change a newer interval. An attempt that started without an interval cannot create evidence for a waiter that starts later. A verdict with no active bounded interval creates no evidence.

The queue verdict remains independent. A late retryable result can retain the record and use the existing retry schedule. A late matching receipt can retire the record. Neither result reopens completed work or invokes a Runtime.

This decision does not add persistence, a receipt store, a second queue, a global timeout, or delivery cancellation.

## Alternatives considered

### Keep evidence on the queue record

Rejected because record lifetime exceeds waiter lifetime. Old attempts can then change evidence that belongs to no waiter or to a later waiter.

### Cancel delivery when the waiter ends

Rejected because the delivery lease prevents duplicate delivery and a late matching receipt can still retire the record correctly. Waiter timeout is not a queue-delivery verdict.

### Persist receipt evidence

Rejected because runtime events are accepted volatile evidence. Persistence would introduce another durable delivery protocol without protecting work-result arbitration.

## Consequences

Timeout diagnostics describe only the active bounded wait budget. A late retryable verdict from an old or ownerless attempt is intentionally absent from a new waiter's diagnostics, while the queued record still follows ordinary retry rules.

Tests must use injected time and an explicitly deferred delivery. They must start a new waiter before the old delivery settles and prove that only attempts admitted under the new generation contribute to its timeout evidence.
