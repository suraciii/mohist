# Issue 615: Runtime Event Outbox Delivery Liveness

## Problem

`AgentSessionRuntimeEventOutbox` aborts a delivery after its configured
deadline, but still awaits the delivery promise. A transport adapter that
ignores `AbortSignal` can therefore leave the shared `kick()` promise pending
forever. Every later enqueue joins that pending drain, including unrelated
Workflow Sessions.

Retrying the timed-out batch is not safe. The Server receives no generic
outbox-record id for streaming events, so the original request may still
commit after the local deadline. A concurrent retry could duplicate a delta or
settle an input twice.

## Proposed Change

Give each runtime-event scheduling group one in-process delivery lease. At a
hard deadline, retain the lease and every durable record in the batch, release
the shared drain, and exclude only that group from later drains. Other groups
continue through the existing one-shot retry mechanism.

When the original promise eventually resolves, process its receipts through
the existing acknowledgement and atomic snapshot path. Only after that path
releases the lease may the group be eligible again. A matching late receipt
therefore settles the original records exactly once locally; a late failure or
non-matching receipt retains them for the normal retry path.

## Non-goals

- Do not add a Server API, change receipt contents, or infer a Workflow result
  from Session/Turn state or task logs.
- Do not delete, re-key, or synthesize a receipt for an unresolved request.
- Do not retry or poll a group whose original delivery promise is still
  unresolved.
- Do not solve a process restart while a non-cooperative transport remains
  outstanding; the existing durable queue remains the recovery source.
