# Self Review

## Scope

Issue #641 is ported to the current master architecture. The current master Runtime Event Queue is intentionally volatile; this change does not restore the deleted durable Outbox, journals, snapshots, cleanup waits, or any Server/API/schema behavior.

## Findings

- The queue owns one optional bounded receipt waiter with the existing task `AbortSignal` and one fixed budget.
- Retryable transport failures, delivery timeouts, empty responses, and non-matching responses retain the current queue record and update in-memory latest-reason evidence.
- Timeout and cancellation remove only the local waiter and evidence. They do not cancel a delivery lease, remove a record, or change unrelated queue records.
- Matching receipts continue to retire the record and resolve the waiter. Existing permanent refusal and already-consumed behavior remains terminal and cannot invent an AgentTurn.
- A late matching response can retire the volatile queue record after the task waiter has ended, but it cannot reopen the task or invoke OpenCode or Pi.
- OpenCode and Pi receive their effective turn budget and task signal for ordinary Workflow input. Timeout and cancellation project as `session-reporting-failed`; other enqueue failures retain their existing classification.
- Attempt and retry evidence is intentionally process-local because current master has no runtime-event persistence. A process restart may lose the queued record and starts a new evidence interval.

## Validation

Focused queue, OpenCode transcript, and Pi action tests pass. Runner production and test typechecks pass. The new queue tests cover continuous retry through expiry, structured reason formatting, record and unrelated-record preservation, cancellation, matching recovery, coalesced waiters, and late settlement. OpenCode and Pi tests cover equivalent timeout classification without Runtime invocation and exact budget/signal wiring.

## Residual Risks

- Queue delivery remains volatile by current master design; process death can lose an undelivered suffix and its evidence.
- A delivery attempt that has already timed out remains leased until its late promise settles, preserving the existing no-duplicate-delivery rule. A task waiter still ends at its own budget.
- The local queue uses fake-timer-controllable `setTimeout`; production scheduling uses the injected `now` seam for elapsed and retry calculations.
