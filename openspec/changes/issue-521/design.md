## Context

The design spec (`design/agent-execution.md`) defines AgentSession as a durable logical session with stable `SessionInput` and `AgentTurn` subrecords, three-valued follow-up results, and idempotent retry. The record types already exist and are persisted (`AgentSessionInputRecord`, `AgentTurnRecord` in `AgentSessionStatusSnapshot.Inputs`/`.Turns`), and the **launch** path fully uses them: the `AgentLaunchCoordinatorGrain` calls `EnsureInitialLaunch` to record the first input+turn at acceptance time, `MarkInitialTurnExecuting` on dispatch, and `MarkInitialTurnTerminal` on the AgentJob result.

The **follow-up** path does not. `AgentSessionFollowupRoutes.ExecuteFollowupAsync` calls `AgentSessionGrain.BeginFollowupAsync()` (which mints an `operationId` lease), dispatches one `ReceiveFollowup` to the runner, and returns `AgentSessionFollowupResult(SessionId, "sent")` only if the runner reports `{accepted:true}`. The follow-up input is never an `AgentSessionInputRecord` — its only durable form is a flat `session.input` transcript event written later by the runner's outbox (`followup-handler.ts:enqueueFollowupInput`). There is no client idempotency key (unlike launch, compact, and reset, which read the `Idempotency-Key` header), and the sync result is binary (`sent` vs transport error), not the spec's `accepted`/`rejected`/`unknown`.

Constraints: Server is the sole authority for binding, activity, and now input/turn identity (`design/architecture.md`). The runner reports physical facts; it does not mint domain identity. Runtime events already flow back correlated by `operationId` (`session.input`, `session.activity`), and the grain already clears the follow-up lease on an idle `session.activity` carrying a matching `operationId` (`AgentSessionGrain.cs:924`).

## Goals / Non-Goals

**Goals:**
- A follow-up accepted by Mohist persists a stable `SessionInput` (Id, sequence, acceptance) synchronously on the Server grain at acceptance, before the response.
- Each follow-up round is a stable `AgentTurn` that consumes one or more inputs in order and progresses `queued → executing → terminal`.
- Idempotent retry: a client call identity resolves to the same `SessionInput`; no duplicate on retry.
- The three-valued `accepted`/`rejected`/`unknown` sync result with stable Input/Turn identity, shared by Web and CLI.
- Input acceptance vs Turn status are separately observable, so "accepted, pending" is distinguishable from "executing".

**Non-Goals:**
- Cancelling a queued turn or stopping an executing turn (issue Non-Goals; Cancel targets the launch turn only today).
- Attachment inputs; Slack thread continuation; context compaction/rewriting (issue Non-Goals).
- Auto-redelivery of queued inputs across an extended runner outage (see Risks).
- Backfilling `SessionInput`/`AgentTurn` subrecords for historical follow-ups (their conversation history remains in the flat transcript).

## Decisions

### D1. Server mints and persists the follow-up `SessionInput` at acceptance — not the runner

The grain creates the `AgentSessionInputRecord` (stable Id, next sequence, text, source, `Acceptance = Accepted`, no `JobId`) inside the accept transition, committed before the response. This mirrors the launch path's `EnsureInitialLaunch` and makes the input identity durable independent of runner delivery. The runner continues to write the flat `session.input` transcript event (it is the conversation log, not the identity record — the design doc keeps transcript flat and subrecords separate, so the two coexist by design).

- Alternative: let the runner remain the recorder (as today). Rejected — a transcript event written after the fact cannot provide a synchronous stable Id to the caller, and a runner failure would lose the accepted input, violating "once accepted, never dropped".

### D2. Idempotency key optional with grain-minted fallback (recovery convention)

The HTTP layer reads the `Idempotency-Key` header (same helper as compact/reset, `AgentSessionRecoveryRoutes.RecoveryIdempotencyKey`). When provided, the grain looks up an existing `SessionInput` by that key and returns it on retry without creating a second input. When omitted, the grain mints a unique per-call key — so omission is **not** retry-idempotent, exactly as the design doc specifies for compact/reset (`agent-execution.md:246-250`). The `IdempotencyKey` is stored on the `AgentSessionInputRecord` for lookup. Web and CLI always generate and send a key so they get retry safety.

- Alternative: require the key (like manual launch, `AgentSessionLaunchRoutes`). Rejected for follow-up because it is a lighter-weight continuation and the optional-with-fallback convention is already established for the other session commands; requiring it adds friction without a domain benefit, since clients that want retry safety simply provide a key.

### D3. Turn assignment: join an unclaimed queued turn, start a new one after dispatch claim or during execution

When a follow-up input is accepted:
- **Idle** (no queued/executing turn) → create a new `AgentTurnRecord` (`queued`) and assign the input.
- A **queued turn with no dispatch claim** exists → append the input Id to that turn's `InputIds` in submission order (one turn consumes multiple inputs).
- A **dispatch-claimed** or **executing** turn exists → create a **new** `AgentTurnRecord` (`queued`) for the input; the claimed or running turn is not interrupted and the input is not merged into it.

The durable dispatch claim seals the turn's `InputIds`: it is the point at which the server has formed the immutable Runner payload. The Runtime has not necessarily emitted `session.input` yet, so the turn remains `queued` for observation, but a later input starts the next queued turn instead of being silently omitted from the already-claimed payload. A turn with multiple inputs is dispatched with its inputs combined in submission order (server-side), so the runner still receives one prompt per dispatch.

- Alternative: always one-input-per-turn. Rejected because the issue's Product Shape explicitly states a turn may consume multiple inputs; the absorption rule captures rapid double-sends before execution starts.

### D4. Decouple acceptance from delivery — `accepted` means "persisted", not "delivered"

`accepted` is returned as soon as the grain persists the `SessionInput`, per the spec ("accepted: Mohist 已持久接受 SessionInput，它可能仍在排队"). Runner dispatch happens after acceptance; a runner-offline or unavailable result no longer reverts acceptance — the input stays accepted and its turn stays `queued` (today such a case returns 503 and the input is lost). This is strictly better for the user and is required for the pending-vs-executing distinction to be meaningful.

- Alternative: keep delivery gating acceptance (return `accepted` only on runner confirmation). Rejected — it conflates acceptance with execution and makes the pending state unobservable.

### D5. Turn status progression reuses the existing `operationId`-correlated events

The grain already receives `session.input` (→ activity active) and `session.activity` idle (→ clears the matching lease) correlated by `operationId`. Extend these two hooks, server-only:
- `session.input` with a follow-up `operationId` → mark the linked turn `executing`.
- `session.activity` idle with a follow-up `operationId` → mark the linked turn terminal (`completed`/`failed`/`unknown`), alongside the existing lease clearing.

The `AgentSessionFollowupLease` gains `InputId` and `TurnId` so `operationId` resolves to the turn. No runner change is needed for correlation.

### D6. Three-valued sync result mapping

- `accepted` — grain persisted the `SessionInput` (synchronous).
- `rejected` — Server confirmed non-acceptance: empty text (400), session not found (404), recovery-in-progress (409), or capacity exceeded.
- `unknown` — Server could not confirm persistence (grain call timed out / response lost). The client must reconcile with the same call identity, never resend with a new one.

The `AgentSessionFollowupResult` changes from `(SessionId, "sent")` to the three-valued `status` plus `inputId` and `turnId`. Both the canonical route and the issue-scoped alias return the same shape.

### D7. Observation surface exposes follow-up input/turn status

The session summary DTO is extended to surface the `Inputs` and `Turns` lists (acceptance + turn status), so Web and CLI can render "accepted, pending" vs "executing". Today only the launch input/turn is observable via `GetInitialLaunchAsync`/`AgentLaunchObservationAssembler`; follow-up subrecords must be readable too.

The `Inputs`/`Turns` observation is a **status/identity view only**; the flat transcript (`session.input` events written by the runner) remains the single source for the conversation text. Clients SHALL render follow-up message text from the transcript and status (acceptance, turn status) from the observation — they SHALL NOT render the input text from the `Inputs` list — so a follow-up is never displayed twice. This mirrors how the launch path already separates launch-status observation from transcript text.

### D8. Lease lifecycle reconciled with synchronous acceptance and multi-turn queueing

The current follow-up lease assumes at-most-one in-flight follow-up: `BeginFollowupAsync` throws `FollowupOperationInProgressException` if any lease is non-accepted, and recovery-idle guards treat any pending follow-up as "active". Both conflict with synchronous acceptance (D4) and multi-turn queueing (D3), where several follow-up turns may be queued or executing at once and a follow-up submitted during execution MUST be accepted and queued (AC3).

Reconciliation:
- **Acceptance collapses Begin+Confirm into one synchronous step.** The accept transition persists the `SessionInput`, assigns/creates the `AgentTurn`, and records an already-`Accepted` lease carrying `InputId`/`TurnId`/`operationId` in a single commit. There is no non-accepted lease window, so the Begin→Confirm→Abandon round-trip is retired for the accept path.
- **The concurrent-followup rejection is removed.** A follow-up is accepted regardless of whether a turn is executing; the turn-assignment rule (D3) decides whether it joins a queued turn or starts a new one. The grain enforces a bounded queued-input/turn capacity instead of a single-flight lease (see capacity note below) — exceeding it returns `rejected`, not an in-progress conflict.
- **The lease becomes per-turn, many at once.** Each in-flight follow-up turn has its own accepted lease used only for `operationId`→turn correlation and clearing on idle `session.activity`. Multiple leases may coexist (one per non-terminal follow-up turn).
- **Recovery-idle guard checks for non-terminal follow-up turns.** Compact/Reset still require the session to have no in-flight work; the guard is expressed as "no queued or executing follow-up turn" rather than "any pending lease". A terminal follow-up turn does not block recovery.

- Capacity bound: the accept transition enforces a bounded count of queued (non-terminal) follow-up inputs/turns — a runtime config constant (not a product-model value, per `agent-execution.md`). Exceeding it returns `rejected` (`capacity_exceeded`), never discarding an accepted input.

### D9. Idempotent retry re-attempts delivery only while the turn is still queued

A retry with the same idempotency key resolves to the same `SessionInput` (no duplicate, per the input spec). Its delivery behavior depends on the turn state:
- If the turn is still `queued` (the original dispatch did not succeed, e.g. runner was offline) → the retry re-attempts runner delivery, moving the turn toward `executing`.
- If the turn is already `executing` or terminal → the retry is pure-identity: it returns the original `inputId`/`turnId` and does NOT re-dispatch (no duplicate runtime work).

This makes retry the client-driven delivery path for a stuck-queued input, without introducing server-side auto-redelivery (which remains a Non-Goal).

## Risks / Trade-offs

- [Queued input is not auto-redelivered if the runner is offline at acceptance] -> The input is durable and observable as `queued`. There is no server-side auto-redelivery (Non-Goal); instead, a client retry with the same idempotency key re-attempts delivery when the turn is still `queued` (see D9). This is not a regression — today an offline runner loses the input entirely.
- [Multi-input-per-turn dispatch combination adds server-side text joining] -> If combination proves complex, fall back to one-input-per-turn; the hard requirement (no merge during execution) is preserved either way, and the model still allows future multi-input turns.
- [`accepted` on offline runner could surprise callers expecting "sent"] -> The result model is explicitly three-valued and documented; clients read turn status to see queued vs executing, so "accepted but queued" is visible, not silent.
- [operationId correlation assumes the runner keeps emitting operationId-correlated events] -> No runner change is required; the correlation is already in place and tested. A runtime that emits no terminal event leaves the turn non-terminal, surfaced honestly (not silently completed).
- [Lease window (5 min) vs long-running turns] -> The lease tracks acceptance/delivery, not turn lifetime; turn terminal is driven by `session.activity`, independent of lease expiry, so a long turn is not prematurely cleared.

## Migration Plan

- No persistence migration. The subrecord types and storage columns already exist and are populated by the launch path; follow-up simply starts populating them. Historical follow-ups remain readable via the flat transcript (their conversation history is intact).
- The `AgentSessionFollowupResult` shape changes (status becomes three-valued; `inputId`/`turnId` added). Web and CLI are updated in the same change. Per project policy (active development, no version compat), no compatibility shim is required.
- Rollback: revert the follow-up route/grain changes; existing launch subrecords and all transcript data are unaffected. Re-accepting a follow-up after rollback returns to the flat-transcript-only behavior.

## Open Questions

- Exact representation of a multi-input turn's dispatch payload (single combined prompt vs ordered list) — resolve during implementation against the runner's single-prompt `ReceiveFollowup` contract.
- Concrete value of the queued-input/turn capacity bound — pick a runtime config constant during implementation; the behavior (reject, not drop) is fixed by D8.
- Whether the observation DTO surfaces the full `Inputs`/`Turns` history or a bounded recent window — decide based on payload size once follow-up subrecords are populated.
