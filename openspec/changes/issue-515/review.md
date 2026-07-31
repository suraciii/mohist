# Issue 515 Review

## Findings

### [P2] Cancelled follow-up turns produce undeliverable terminal events

Location: `packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:1858-1865`
(`ResolveFollowupTurnTerminalStatus` maps `"cancelled"` → `AgentTurnStatus.Cancelled`);
`packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs:2410`
(`TryEmitFollowupDeliveryAsync` sets `status = turn.Status.ToString().ToLowerInvariant()`);
`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackTerminalDeliveryHandler.cs:128`
(`SlackTerminalDelivery.Validate` rejects any status other than `"completed"`, `"failed"`,
`"unknown"`).

When a follow-up turn is cancelled via a runtime `session.activity` event carrying
`status: "cancelled"`, `ResolveFollowupTurnTerminalStatus` produces
`AgentTurnStatus.Cancelled`. The `AppendEventsAsync` terminal-transition detector then fires
`TryEmitFollowupDeliveryAsync`, which emits a `AgentSessionFollowupDelivery` CloudEvent with
`status = "cancelled"`. The `SlackFollowupDeliveryHandler` delegates to
`SlackTerminalDeliveryHandler.HandleAsync`, which calls `delivery.Validate()`. That method
throws `InvalidOperationException` because `"cancelled"` is not in the accepted set. The
dispatcher catches and logs the exception, so no outbox row is created and the user never
receives the cancellation result in the thread. This violates AC1 ("Bot replies in same thread
with final result") for the cancellation case. Fix: map `AgentTurnStatus.Cancelled` →
`"failed"` in `TryEmitFollowupDeliveryAsync`, or add `"cancelled"` to the valid statuses in
`SlackTerminalDelivery.Validate()` and handle it in `Render`.

### [P2] `HandleAmbiguousNonOwnerAsync` lacks unique-index conflict handling

Location: `packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1432`
(`EnqueueReplyAsync` call); `packages/server/src/Mohist.Server/Infrastructure/Slack/SlackOutboxStore.cs:61-120`
(`EnqueueAsync` has no `DbUpdateException` catch for the new unique index).

`HandleAmbiguousNonOwnerAsync` enqueues the owner-rejection message via `EnqueueReplyAsync`
(→ `EnqueueAsync`), not `EnqueueRequiredReplyAsync`. Unlike `EnqueueRequiredAsync`, which now
catches `DbUpdateException` from the new `UX_SlackOutboxRows_ConnectionId_DispatchRef_Kind`
unique index, `EnqueueAsync` has no such guard. Under concurrent Slack redelivery of the same
ambiguous non-owner event, `TryClaimAsync`'s winner-retry delivery check queries a separate
`DbContext` and may not see the in-flight outbox insert, so both callers reach
`EnqueueReplyAsync` with the same `DispatchRef`. The second insert violates the unique index,
`DbUpdateException` propagates unhandled, and the ingress call returns HTTP 500. The event is
not acknowledged and Slack retries unnecessarily. Fix: use `EnqueueRequiredReplyAsync` (which
has conflict handling) instead of `EnqueueReplyAsync`.

### [P2] Follow-up terminal delivery path is completely untested end-to-end

Location: `packages/server/tests/Mohist.Server.SpecTests/Specs/Sessions/AgentSessionGrainFixture.cs:75`
and `AgentSessionFollowupConcurrencyFixture.cs:136` (both inject `NoopEventStore`).

Both grain test fixtures register `NoopEventStore`, whose `AppendAsync` is a no-op. This means
`TryEmitFollowupDeliveryAsync` silently discards every `AgentSessionFollowupDelivery` event in
all grain specs. No test verifies that a non-launch turn terminal transition emits the event,
that `SlackFollowupDeliveryHandler` enqueues a thread-targeted outbox row, or that the emission
is idempotent across reactivations. The cancelled-status bug above (P2) would not be caught by
the current suites. The Slack channel thread ingress specs verify inbox/outbox/binding behavior
but never drive a follow-up turn to terminal and check the delivery outbox.

## Verification

- Server SpecTests: 3544/3544 passed.
- Server UnitTests: 1675/1675 passed.
- Server ArchTests: 51/51 passed.
- Slack adapter typecheck and tests: 8/8 passed.

<promise>FAIL</promise>
