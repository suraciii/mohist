# Self-Review — Issue #492

Reviewer verdict: **FAIL** — the buildable design is sound and well-grounded, but two
plan artifacts contradict each other on the highest-risk property of this issue
(exactly-once input on reconnect recovery), and the proposal Impact section is
stale relative to the chosen design. Both must be reconciled before building.

## Verification method

I cross-checked every load-bearing factual claim in `design.md` against the
current source. All of them are accurate:

- Disconnect is one-sided: `RunnerHub.OnDisconnectedAsync` (`RunnerHub.cs:36-45`)
  calls `RunnerDisconnectedAsync` (`AgentSessionGrain.cs:1311-1319`), which flips
  **only** `Active → Unknown` (`:1314` guards on `Activity == Active`).
  `OnConnectedAsync` (`RunnerHub.cs:21-32`) only registers + updates the build
  hash — no reconciliation. ✓
- Cancel never settles activity: `AgentSessionCancelRoutes.cs:60-146` is a pure
  SignalR invoke; `CancelHandlerDeps` (`cancel-handler.ts:34-38`) has no outbox;
  `handleCancel` (`:49-118`) returns the reply and stops. ✓
- Recovery requires idle: `EnsureSessionIdleForRecovery` (`AgentSessionGrain.cs:552-558`)
  throws on non-idle, gating `RecoverMissingRuntimeSessionAsync` (`:177-190`) and
  Compact/Reset. `RunnerRoutes.cs:344,473` return `agent_session_recovery_conflict`. ✓
- `session.activity { idle }` already settles `Unknown → Idle` with no prior-state
  guard: `ApplyRuntimeEventToDomain` (`:1459-1461`) → `ParseActivity` (`:1482-1488`).
  Note the existing `Unknown`-drops-`session.input` rule (`:839-841`) does **not**
  touch `session.activity`, so the chosen channel is correct. ✓
- Binding guard discards superseded facts: `AppendEventsAsync(..., requireCurrentRuntimeBinding: true)`
  (`:826-837`). ✓
- `RebindRuntimeSession` (`AgentSession.Transitions.cs:180-208`) requires idle + CAS
  via `EnsureExpectedRuntimeBinding` (`:213-220`); `"missing-recovery"` is a valid
  reason (`:191`). ✓
- Convergence is workflow-only: `onDispatchReconnected` (`host.ts:350-362`) runs
  `runConvergenceOnce`/`runCleanupOnce`; no AgentSession-binding pass. ✓
- `RunnerId` is a durable column (`AgentSessionRow.cs:7`, written at
  `AgentSessionStore.cs:160`); in-memory `RunnerConnectionTracker._sessions` is
  cleared on disconnect (`RunnerConnectionTracker.cs:29-36`); no runner-scoped
  session query exists today. ✓
- Existence-check drift confirmed: `opencode/runtime.ts:134-137,293-296,313-319,369-372`
  use `as never`; the typed contract already exists in `turn.ts:264-267`
  (`session.get({ sessionID, directory }, { throwOnError: true })`) and the
  active-turn status map read in `turn.ts:347-357`. `readCancelFacts` defaults
  `stopConfirmed` to `true` (`command-runtime.ts:145,152`). Pi's `isStreaming` is
  the active-turn signal (`pi/runtime.ts:293,354`), and only Pi can report an
  unconfirmed stop (`pi/runtime.ts:283-311`). ✓
- `BindingProbeResult` is binary today (`binding-recovery.ts:15-17`); only
  `kind === "missing-session"` authorizes recovery (`:50-51`); present → no
  second candidate (`:50`). ✓

The architecture (Runner-driven reconcile reusing the convergence loop, durable
runner-scoped query, happy-path settle via the existing `session.activity`
channel with zero new grain logic, confirmed-missing settle+rebind under one CAS,
binding-guarded cancel fact, typed-contract migration) is coherent and reuses
existing machinery well. Issue acceptance criteria are all covered by the three
specs + three tasks; task ordering (T-003 depends on T-001's enriched probe) is
correct.

## Finding A (BLOCKER) — spec and task contradict on exactly-once input for reconnect recovery

`runner-reconnect-reconciliation/spec.md` mandates input submission on reconnect
recovery, while `design.md` (D4) and `tasks.json` (T-003) explicitly forbid it.

- **Spec requirement** (`specs/runner-reconnect-reconciliation/spec.md:36`):
  "Recovery SHALL ... **SHALL submit the current input exactly once**."
- **Spec scenario** (`specs/runner-reconnect-reconciliation/spec.md:44-48`,
  "Recovery submits the current input exactly once"): "WHEN confirmed-missing
  recovery runs on reconnect THEN **the triggering input SHALL be submitted
  exactly once** against the confirmed replacement".
- **Design D4** (`design.md:62`): "**No input is submitted** on a bare reconnect
  (there is no triggering input); the next task/follow-up submits input exactly
  once ... via the unchanged `resolveOrRecoverBinding` path."
- **Task T-003** (`tasks.json`): description "No input is submitted on bare
  reconnect"; acceptance criterion "no input is submitted on bare reconnect —
  **verified by spec test**."

This is a direct contradiction on a SHALL, on the single most safety-critical
invariant of this high-risk issue. Both sides are written as spec-testable
behavior, so they cannot both be built.

The design is the correct side. The reconnect reconcile query (D2 / T-003)
projects only `{ sessionId, runtime, runtimeSessionId, workDir }` — it carries
**no input payload** — and reconcile is triggered by SignalR reconnect in
`onDispatchReconnected`, independent of task/follow-up lifecycle. Submitting a
task's input from the reconcile layer would require the Runner to reach into
Workflow task state, which the issue's non-goals forbid ("do not change Workflow
retry/rerun semantics"). So the literal spec requirement is unsatisfiable from
the reconcile path; the exactly-once invariant is in fact preserved by the
retrying task layer probing the rebound binding (present → submit once, no replay).

**Required fix (spec only):** bring `runner-reconnect-reconciliation/spec.md`
into line with D4. The reconnect requirement/scenario should state that bare
reconnect submits no input (there is none), and that when a task/follow-up input
is pending it is submitted exactly once by that task/follow-up against the
confirmed replacement, never replayed by reconnect or retry. The "create at most
one candidate + confirm replacement" clauses are fine as-is.

## Finding B (secondary) — proposal Impact section is stale vs the chosen design

`proposal.md` Impact (lines 22-23) describes two mechanisms that the design
explicitly considered and rejected. The tasks follow the design, not the
proposal, so this is a coherence/clarity defect rather than a build conflict —
but it will mislead a builder who reads the proposal first.

- **Reconcile trigger location.** Proposal: "`OnConnectedAsync` enumerates
  sessions bound to the reconnecting Runner and triggers reconciliation."
  Design D1 (`design.md:36-42`) rejects hub/`OnConnectedAsync`-orchestrated
  reconcile and selects Runner-driven reconcile via the existing convergence loop
  (`onDispatchReconnected`), with the Runner pulling the list (D2).
  `OnConnectedAsync` is **not** modified. Tasks follow D1.
- **Cancel fact recording.** Proposal: "the cancel route records the
  confirmed-stopped activity fact." Design D5 (`design.md:68-78`) rejects
  route-side settle (alternative (a)) and instead adds the outbox to the
  Runner-side `CancelHandlerDeps` so the cancel-handler records the
  binding-guarded fact. Tasks follow D5.

**Required fix (proposal only):** correct the Impact bullets to match D1/D5
(Runner-driven reconnect pull; cancel-handler-side outbox fact, not the API
route).

## Coverage note (positive)

All eight issue acceptance criteria map cleanly onto the three specs and three
tasks. The non-recovery failure set (timeout / transport / unavailable / corrupt
/ uncertain-input / active / unresolved-unknown) is exhaustively enumerated in
`runtime-binding-recovery/spec.md` and reflected in T-003 acceptance. The
superseded-binding invariant is covered generically across all three specs and
relies on already-verified CAS/outbox guards. No acceptance criterion is left
without a spec scenario or a task.

<promise>FAIL</promise>
