## Why

The Runner's runtime-event outbox is already above its 5,000-record retention target because the current policy can discard only streaming deltas; protected tool-call, usage, binding, input, and activity facts continue to accumulate. This leaves the Runner in a warning-only overload state and can stall newly admitted Workflow sessions, while a single blocked or mismatched receipt can delay unrelated workflow sequences.

## What Changes

- Define a bounded runtime-event outbox policy that covers protected records as well as streaming deltas, with an explicit admission, backpressure, or other bounded outcome when protected capacity is exhausted.
- Keep `session.input` and terminal `session.activity` records fail-closed: timeout, transport failure, empty or malformed response, and mismatched receipt retain the exact record and identity for replay rather than dropping or fabricating a result.
- Establish explicit rules for high-volume `tool_call.*`, `usage.updated`, and binding-reconciliation facts. Coalescing or compaction is allowed only when its delivery identity and Server-visible transcript invariants are defined and preserved; otherwise the record remains protected and capacity pressure is surfaced.
- Isolate delivery scheduling by logical sequence so a stalled, rejected, or unmatched group cannot prevent an unrelated Workflow group from progressing. Each group continues to preserve FIFO ordering and receipt matching, including late responses after a delivery lease or timeout boundary.
- Make producer and observer paths propagate protected-capacity admission failures as explicit, awaitable outcomes through the durable enqueue, runtime event reporting, and terminal settlement boundaries. Do not silently convert an enqueue failure into task success, failure, or replacement.
- Add structured, aggregated diagnostics that distinguish protected-record pressure, receipt mismatch, and transport or timeout failure without emitting one repeated warning per retained record.
- Add deterministic focused regressions for over-cap protected records, exact receipt replay, compaction invariants, cross-group liveness, lease/late-receipt behavior, and bounded overload handling.
- **BREAKING**: Runner runtime-event producer contracts may change where synchronous or fire-and-forget observer callbacks currently cannot report protected-capacity admission failures to the owning execution.

## Capabilities

- `runtime-event-outbox-retention`: Bounded runtime-event storage and admission behavior, protected-record classification, fail-closed receipt retention, safe compaction rules, and structured overload diagnostics.
- `runtime-event-delivery-liveness`: Per-sequence delivery isolation, FIFO and receipt matching, timeout/late-receipt settlement, and progress for unrelated Workflow sequences when one sequence is blocked.

## Impact

- Runner outbox implementation and ports under `packages/runner/src/server`, including the durable `.mohist/runner-state/runtime-events.json` snapshot, delivery adapter, retry scheduling, and retention configuration.
- Runner Workflow and AgentSession reporting paths under `packages/runner/src/actions` and `packages/runner/src/runtime`, including OpenCode/Pi event observers, input admission, cleanup/activity reporting, and terminal result propagation.
- Server AgentSession runtime-event endpoints and receipt/acceptance contracts may need coordinated changes if bounded admission or batch/compaction metadata is exposed. Existing transcript semantics and exact execution identities remain authoritative.
- Runner health/admission behavior and operational diagnostics will change under protected-record pressure; unrelated Web, CLI, Workflow ownership, `AgentResultSettlement`, and terminal-result arbitration are outside this change.
- No new external dependency or live outbox cleanup/restart operation is required; focused Runner tests and any affected Server contract tests must cover the new boundaries.
