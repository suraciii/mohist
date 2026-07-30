## Context

Issue 522 splits "make it stop" into two distinct commitments: **cancel** a Turn that has not started executing, and **stop** (request halt of) a Turn that is executing. Today only one control exists end-to-end — `POST /agent-sessions/{id}/cancel` → `CancelAgentSession` SignalR → runner `session.abort()`. It always interrupts a *running* Runtime session, so it is semantically a **stop**: it cannot remove a queued Turn, addresses the whole Session rather than a Turn, and a stale entry can mis-stop newer work. Its reply vocabulary also conflates the two acts — a confirmed stop is reported as `state: "cancelled"`.

Two structural facts shape this design:

1. **Only the initial launch Turn is persisted.** `new AgentTurnRecord(` exists at exactly one site — `EnsureInitialLaunch` (`AgentSession.Transitions.cs:415`). `ConfirmFollowupAsync` does not record a Turn. So a follow-up that starts a new idle turn has no durable Turn identity and no `Queued` status to cancel. The design invariant "Session has at most one active Turn" bounds the work, but the Turn must first be recorded.
2. **AgentJob is the first-Turn terminal authority; later Turns are not.** `AgentJobGrain.EnterTerminalStateAsync` maps the first Turn terminal (`AgentJobGrain.cs:1352`), and `MarkInitialTurnTerminal` no-ops on `Cancelled`/terminal and sets activity to `Idle` for a cancelled turn (`Transitions.cs:492-519`). `AgentJobStatus` has no `Cancelled`; the terminal mapper forces every non-`Completed` first-Turn outcome to `Failed`. A later Turn's stop/cancel must not rewrite an already-terminal Job.

The change implements `agent-turn-cancel`, `agent-turn-stop`, and `agent-turn-control-surface`. Server remains the state authority; Runner reports facts only. AgentSession owns Input/Turn/activity/transcript/binding; AgentJob owns the first work result. No cross-aggregate transaction is introduced.

## Goals / Non-Goals

**Goals:**

- Let a caller deterministically cancel a not-yet-executing AgentTurn on the Server, with no Runner or Runtime contact.
- Let a caller request a Runtime stop of an executing AgentTurn, presented honestly as a request whose effect depends on convergence.
- Make an unconfirmed stop a formal `Unknown` state: no new Turn, no SessionInput replay, no synthesised idle/completed/failed verdict.
- Address every cancel/stop at a specific Turn; a stale entry reports "Turn already ended" and never affects a later Turn.
- Have the first Turn's cancel adjudicated by its AgentJob as a cancelled terminal verdict; leave later Turns and already-terminal Jobs untouched.
- Present one shared vocabulary — cancelled, stop-requested, stopped, unknown — across Web and CLI, with distinct cancel/stop operations.

**Non-Goals:**

- Guaranteeing the Runtime halts within any deadline.
- Rolling back external side effects an Agent already produced.
- Deleting or revoking already-accepted SessionInput.
- A Slack-side stop entry or its authorization.
- Full historical multi-Turn scheduling/queueing beyond the single active Turn the model already permits.
- Making AgentTurn or SessionInput a top-level addressable resource.

## Decisions

### D1. Record a Turn (Queued) when a follow-up starts an idle turn

Extend `EnsureInitialLaunch`'s child-record pattern to the follow-up idle-start path. When `BeginFollowupAsync`/`ConfirmFollowupAsync` starts a new idle turn (`StartsIdleTurn`), the Session grain mints a stable Turn id, persists an `AgentTurnRecord` with status `Queued` linked to the accepted SessionInput, and sets activity to `active` **before** the follow-up is dispatched to the Runner. The Runner continues to report only facts correlated to that Turn id; it does not create the Turn.

This is bounded by the "at most one active Turn" invariant: there is never a queue of executing Turns, only the single current Turn moving `Queued → Executing → terminal`. Historical Turns need not be retroactively recorded — only the current Turn must exist to be cancellable.

**Rationale:** a queued Turn must have a durable identity and a `Queued` status to be cancelled authoritatively on the Server. Without this, cancel only covers the initial launch Turn's brief pre-dispatch window, which does not satisfy the common "I sent a follow-up, I want it back" case. Recording the Turn at acceptance (Server authority) rather than at Runner attach matches the issue-512 launch principle.

**Alternatives considered:** deferring follow-up Turn recording to a separate issue was rejected because it makes cancel near-useless in practice; recording Turns only in the transcript was rejected because transcript facts cannot enforce current status or identity.

### D2. Generalize Turn transitions from "InitialTurn by jobId" to "any Turn by turnId"

Rename and generalize the `MarkInitialTurn*` surface into Turn-id-keyed transitions on the Session grain:

- `MarkTurnExecutingAsync(turnId)` — `Queued|Unknown → Executing`; no-op if already past Queued (today's `MarkInitialTurnExecuting` behaviour, `Transitions.cs:444`).
- `MarkTurnTerminalAsync(turnId, status, result)` — applies `Completed|Failed|Unknown|Cancelled`; no-op if already terminal; activity converges as today (`Queued→keep`, `Unknown→Unknown`, else→`Idle`).
- `CancelTurnAsync(turnId)` — the new deterministic path: `Queued → Cancelled`, activity → `Idle`, no Runner contact.

The existing jobId-keyed entry points used by `AgentJobGrain` become thin resolvers that look up the launch Turn id from the Job's stored `InitialTurnId` and delegate. The state machine, no-op guards, and `CurrentTurnEndedAt` handling are reused unchanged.

**Rationale:** cancel/stop target a Turn, not a Job; the transition surface must speak Turn id. Generalizing (rather than adding a parallel path) keeps one write authority and one set of guards.

**Alternatives considered:** keeping jobId-keyed methods and adding a separate Turn-keyed cancel was rejected because it leaves two transition vocabularies for the same child record.

### D3. First-Turn cancel is adjudicated by AgentJob; later-Turn cancel is Session-only

Add `AgentJobStatus.Cancelled` and treat it as terminal (`IsTerminal => Completed or Failed or Cancelled`; `Unknown` stays non-terminal). The cancel routing branches on whether the target Turn is the launch Turn (its `AgentTurnRecord.JobId` is set):

- **Launch Turn:** the Session grain delegates to `AgentJob.CancelAsync()`. The Job serializes cancel against dispatch in the same grain: if still `Pending` (Turn still `Queued`), it transitions `Pending → Cancelled` terminal, stages no dispatch, then calls back `MarkTurnTerminalAsync(initialTurnId, Cancelled, …)`. If the Job already dispatched (Turn `Executing`), cancel is rejected — the caller must use stop. This keeps AgentJob the sole first-Turn terminal authority and closes the cancel/dispatch race at the grain.
- **Later Turn:** the Session grain applies `CancelTurnAsync(turnId)` directly. No AgentJob is contacted and no already-terminal Job is rewritten.

The terminal mapper at `AgentJobGrain.cs:1352` gains a `Cancelled → AgentTurnStatus.Cancelled` branch; the existing `Cancelled`-no-op and `Idle`-activity behaviour in `MarkTurnTerminal` already match.

**Rationale:** cancel means "never executed", which is distinct from `Failed` ("attempted and errored"). A real `Cancelled` status keeps capacity, list, and observation filters honest. Routing the launch-Turn cancel through the Job preserves the established ownership boundary and makes dispatch-vs-cancel atomic.

**Alternatives considered:** modelling cancel as `Failed` + `failureCategory: "cancelled"` (which `docs/agents.md` historically promised) was rejected because `Failed` mis-characterises work that never ran and tangles capacity/terminal assumptions; having the API route call the Job directly for launch Turns was rejected because it bypasses the Job's dispatch serialization and reopens the race.

### D4. Repurpose the existing cancel path as Stop; keep its honesty machinery

The current `POST /agent-sessions/{id}/cancel` route, the `CancelAgentSession` SignalR method, and `cancel-handler.ts` become the **stop** path. Changes are additive:

- Accept a target `turnId` (D5) and resolve the Turn's current status. Stop applies only to an `Executing` Turn; a `Queued` Turn is rejected with "use cancel", and a terminal Turn returns "Turn already ended".
- The runner keeps its binding-guarded abort (`abortAndConfirmSession` / `abortAndDiagnose`) and its `recordCancelActivity` outbox fact (`cancel-handler.ts:141`), which already converges activity to `idle` on a confirmed stop and `unknown` on an unconfirmed stop. No new convergence logic is needed.
- A first-Turn stop continues to be adjudicated by the AgentJob through the normal result-report path (the interrupted work reports a terminal result); an unconfirmed first-Turn stop flows through the existing `EnterUnknownStateAsync` → `MarkTurnTerminal(Unknown)` path. Later-Turn stop never touches the Job.

**Cancel never reaches the Runner.** The cancel path is a Server-only grain transition (D2/D3); there is no `cancel` SignalR method.

**Rationale:** the stop infrastructure (reply path, `interruptUnconfirmed`, binding-guarded activity convergence) is already correct and tested; rebuilding it would discard working honesty machinery. Splitting is therefore a vocabulary/targeting change on top of the existing stop, not a new transport.

**Alternatives considered:** a single unified `interrupt` verb that auto-routes queued-vs-executing was rejected because the issue deliberately separates two commitments with different guarantees (deterministic vs. best-effort).

### D5. Address the Turn as a Session child; stale-guard at the grain

Cancel and stop carry `turnId` in the request body of the session-scoped endpoints (`POST /agent-sessions/{sessionId}/cancel` and `…/stop`), not as a path segment. The Session grain resolves the Turn by id and applies the stale-guard:

- Turn not found or already terminal (`Completed|Failed|Cancelled|Unknown`) → return `turn-already-ended` with the Turn's terminal status; issue **no** Runner request and touch **no** other Turn. (`Stopped` is not a Turn status — the `AgentTurnStatus` enum is `Queued|Executing|Completed|Failed|Unknown|Cancelled`; a stopped Turn's status is driven through Completed/Failed/Unknown, and `stopped` is only a reply label, see D6.)
- Turn non-terminal but not in the operation's required state → return the honest current state and the correct verb (`queued → use cancel`, `executing → use stop`).
- Turn in the matching state → apply the operation to that Turn only.

The runner stop payload carries the `turnId` for correlation/logging only; the Runtime abort remains session-scoped because both supported runtimes abort by physical session.

Before sending that session-scoped abort, the Session grain atomically records a stop claim for the executing target. While the claim is held, the grain rejects a follow-up that would begin a later Turn. A retry for the same claimed Turn may reissue the stop, but a different Turn cannot start until the Runner reply has settled the claim. This keeps a terminal event for the old Turn from opening the Session to newer work in the interval before the old Runtime abort is sent.

**Rationale:** the design contract (issue-512 D5, `design/agent-execution.md:194`) fixes Input and Turn as Session children accessed through their Session — a `/turns/{turnId}` path resource would violate it. The stale-guard is enforced at the grain (the single write authority) so every entry point — Web, CLI, issue-scoped alias — shares one guard.

**Alternatives considered:** a top-level `/agent-turns/{id}` resource was rejected as a second addressable aggregate; enforcing the stale-guard only in the HTTP route was rejected because it bypasses direct grain callers.

### D6. One vocabulary and two CLI/Web verbs

Fix the outcome labels across the API reply, CLI output, and Web UI:

| Situation | Label |
|---|---|
| Queued Turn cancelled on the Server | `cancelled` |
| Stop request sent, Runtime has not confirmed halt | `stop-requested` |
| Stop confirmed by the Runtime | `stopped` |
| Stop could not be confirmed | `unknown` |

The runner stop reply is remapped so a confirmed stop reports `stopped` (not `cancelled`); an unconfirmed stop reports `unknown` with `interruptUnconfirmed: true` retained for the API. The synchronous "we sent the request" framing is `stop-requested`; each runtime decides per its abort semantics whether it can answer `stopped` synchronously (OpenCode, authoritative) or must show `stop-requested`/`unknown` (Pi).

CLI exposes two operations under `session`: `mo session cancel <id>` (deterministic cancel of a queued Turn, Server-only) and `mo session stop <id>` (Runtime stop request for an executing Turn). The current `mo session cancel` semantics change from stop to cancel — **BREAKING**, accepted because the project is under active development and the issue requires the two commitments to be distinguishable. Web adds a Turn-level control in the coder-session widget using the same labels and the same verification entry for `unknown`.

**Rationale:** the issue requires Web and CLI to explain the four states identically; a single label set sourced from the Server reply prevents drift. Two CLI verbs match the issue's product shape ("cancel what hasn't started; request stop for what's running").

**Alternatives considered:** keeping `cancel` as the stop verb and adding `abort` for cancel was rejected as inverted from the issue's language; one polymorphic verb was rejected under D4.

### D7. Test at ownership boundaries

Server specs use the in-process grain cluster, in-memory SQLite, fake `TimeProvider`, and a fake dispatch/result observer. They cover: follow-up Turn recorded as `Queued` before dispatch; deterministic cancel with no Runner connected; cancel dispatch-race (cancel wins and loses vs. `AgentJob` dispatch); first-Turn cancel → `AgentJobStatus.Cancelled` and Turn `Cancelled`; later-Turn cancel leaves a terminal Job unchanged; stop of an executing Turn (confirmed → `stopped`/`idle`, unconfirmed → `unknown`); no new Turn or SessionInput replay after an unconfirmed stop; stale entry returns `turn-already-ended` and does not stop a later Turn; non-launch Turn promoted `Queued → Executing` on `session.input` and terminated on a terminal `session.activity`; record/transcript preservation after cancel and stop. Runner tests inject a fake Runtime and assert the stop reply vocabulary and the binding-guarded activity fact. Web tests assert the four labels and the unknown verification entry; CLI tests assert both verbs and reused output shape. No test uses a real Runtime, network, process, filesystem, or clock.

### D8. The Session grain drives non-launch Turn status from runtime facts

The launch Turn's Executing and terminal transitions are driven by AgentJob today (`AgentJobGrain.cs:235,362,810,1324`); runtime events only set activity (`ApplyRuntimeEventToDomain`). For Turn-status-based cancel/stop to apply to follow-up Turns, those Turns must also move `Queued → Executing → terminal`, with one authority and no new Runner contract.

The Runner already emits the two facts a follow-up Turn needs, through `followup-handler.ts`: a `session.input` event before it invokes the runtime (line 332) and a terminal `session.activity` event (`status: completed|failed`, `activity: idle|unknown`) when the turn ends (lines 335-368). The Session grain — already the Turn-status write authority — derives the non-launch Turn transitions from these existing facts:

- `Queued → Executing`: when the grain processes a `session.input` runtime event for the current Turn, it promotes that Turn to Executing.
- `Executing → terminal` (`Completed|Failed`): when the grain processes a terminal `session.activity` for the current Turn, it marks that Turn terminal — **but only if that Turn has no `JobId`** (a non-launch Turn) — in addition to converging activity as today. A launch Turn's terminal stays solely AgentJob-driven (see the guard below).
- Stop → terminal: the stop path's `session.activity` (`idle|unknown`) drives the current non-launch Turn to terminal through the same path; an unconfirmed stop leaves it `Unknown`, matching activity.

The `Queued → Executing` promotion is inherently safe for the launch Turn: issue-512 removed its Runner `session.input`, so it never fires for it. The terminal marking is **not** automatically safe and requires a guard. `AppendTerminalCloseAsync` (the AgentJob's terminal delivery for the launch Turn) ingests its `session.activity` through the same `AppendEventsAsync` → `ApplyRuntimeEventToDomain` path (`AgentSessionGrain.cs:833`), and `EnterTerminalStateAsync` awaits it at `AgentJobGrain.cs:1323` **before** the authoritative `MarkInitialTurnTerminal` at `:1324`. Without a guard, the thin activity-driven terminal would land first and the authoritative result would no-op on the terminal guard, dropping the launch Turn's `message`/`output`/`failureCategory`/`exitCode`. The guard: the activity-driven terminal MUST skip any Turn whose `JobId` is set (a launch Turn); only non-launch Turns (no `JobId`) are terminal-marked from a terminal `session.activity`. With the guard, AppendTerminalClose's `session.activity` leaves the launch Turn's status untouched (only setting activity), so the subsequent `MarkInitialTurnTerminal` applies the authoritative result unchanged. Guarding by the Turn's `JobId` is preferred over inspecting the event's `agentJobId`/`deliveryId` markers because ownership is the invariant: any Turn owned by an AgentJob is terminal-adjudicated by that Job, regardless of which `session.activity` source reaches the grain.

**Rationale:** Turn-status-based eligibility requires Turns to actually pass through Queued/Executing/terminal. The Session already observes the runtime facts that distinguish these states and is the Turn-status authority, so it is the natural driver for non-launch Turns; reusing the existing `session.input`/terminal-`session.activity` emissions adds no Runner contract.

**Alternatives considered:** a new per-Turn Runner `turn-started`/`turn-ended` event was rejected as a new contract for signals the Server already receives; basing eligibility on activity alone was rejected because it loses the per-Turn addressing and stale-guard the issue requires; driving Executing at Server delivery time was rejected because follow-up delivery is synchronous and would erase the Queued window.

**Limitation (follow-up cancel):** follow-up input is delivered to the Runner synchronously at confirmation, so a cancelled follow-up Turn may still be processed by a runtime that already received it — the Server marks the Turn Cancelled and will not drive it further, but cannot un-deliver. A related consequence: after a follow-up-Turn cancel the Server marks activity `idle`, so a new follow-up may be accepted and dispatched while the runtime still holds the cancelled input; the runtime serializes inputs (no true concurrency), but the Server/runtime views diverge briefly until the cancelled input's late `session.input`/terminal facts arrive as no-ops. Launch-Turn cancel remains fully deterministic (it prevents dispatch). This tradeoff is accepted; withholding follow-up cancel entirely was rejected because marking the Turn Cancelled still gives the caller an honest, stoppable target and a clean record, and an already-executing follow-up Turn is stoppable through D4.

## Risks / Trade-offs

- [Follow-up Turn recording is a new prerequisite that touches the follow-up acceptance path] -> Scope it to the idle-start case only; reuse the `EnsureInitialLaunch` child-record shape and the "at most one active Turn" invariant; cover acceptance-then-cancel and acceptance-then-dispatch in specs.
- [Reversing first-Turn cancel to Session→Job is a new cross-grain direction] -> The Job remains the first-Turn terminal authority and serializes cancel vs. dispatch; the callback is the existing `MarkTurnTerminal` shape, so only the initiator direction is new.
- [`AgentJobStatus.Cancelled` is a new terminal enum value seen by list/capacity/observation] -> Update every enum projection and `IsTerminal` deliberately; add architecture tests that only the Job resolves first-Turn terminal from its own facts.
- [Stop vocabulary change is a wire-visible break for any existing cancel consumer] -> Accepted under active development; the runner reply and HTTP response move to `stopped`/`unknown`/`stop-requested`, and `interruptUnconfirmed` is retained.
- [A queued Turn cancelled just as the Runner begins executing could race the Runner's executing report] -> The grain's terminal no-op guard (`Transitions.cs:492`) already makes a late `Executing` report a no-op once `Cancelled`; cancel is adjudicated before dispatch for launch Turns and before Runner contact for later Turns.
- [`stop-requested` vs `stopped` synchronous availability differs per runtime] -> Each runtime reports only what it can confirm; the label is never fabricated and `unknown` is always honestly available.
- [Follow-up cancel cannot un-deliver a synchronously delivered input] -> Launch-Turn cancel stays deterministic (it prevents dispatch); follow-up cancel marks the Turn Cancelled and stops the Server driving it, with the limitation documented in D8; an already-executing follow-up Turn remains stoppable through D4.
- [Driving non-launch Turn status from runtime facts couples Turn terminal to session.activity] -> Both transitions are idempotent no-ops once terminal, the launch Turn is unaffected (issue-512 removed its Runner session.input so the activity-driven path never fires for it), and a missing terminal fact leaves the Turn Executing under the existing Unknown reconciliation rather than fabricating a terminal.

## Migration Plan

1. Add `AgentTurnRecord` recording on the follow-up idle-start path (D1), the Turn-id-keyed transition surface (D2), and the Session-grain lifecycle driving for non-launch Turns from `session.input`/terminal-`session.activity` facts (D8), preserving current `MarkInitialTurn*` callers via delegation.
2. Add `AgentJobStatus.Cancelled`, the `Cancelled` terminal branch, and `AgentJob.CancelAsync`; wire first-Turn cancel delegation (D3).
3. Add `turnId` to the cancel/stop endpoints and implement the grain stale-guard (D5); split the route into `cancel` (Server-only) and `stop` (existing runner path) (D4).
4. Remap the runner stop reply and HTTP response to the shared vocabulary; add `mo session stop` and repurpose `mo session cancel`; add the Web Turn-level control (D6).
5. Add server, runner, Web, and CLI regression coverage; remove the old conflated `state: "cancelled"` for confirmed stops (D7).
6. Deploy Server before clients. Older clients calling the previous `cancel` semantics receive the honest new behaviour (cancel applies only to a queued Turn). Roll back by reverting the complete change; persisted Sessions/Jobs/Turns remain readable through current routes, and no historical transcript data is rewritten.

## Open Questions

- Whether `stop-requested` should be a persistent Turn status (observable on read) or only the synchronous stop-reply label that resolves to `stopped`/`unknown` via activity convergence — left to implementation, provided the read surface never fabricates `idle` for an unconfirmed stop.
