# Self-Review — Issue #492

Reviewer verdict: **PASS** — the plan is ready to build. Every load-bearing factual
claim in `design.md` was verified against the current source and is accurate, the
two contradictions flagged by the prior review have both been resolved in the
artifacts, and the three tasks implement a coherent, CAS-guarded design that
satisfies every issue acceptance criterion. Three non-blocking observations are
recorded below for optional cleanup.

## Verification method

I read the issue, then independently re-derived each factual claim in
`design.md` against the source (not trusting the design's own citations). Every
claim holds:

- **Disconnect is one-sided.** `RunnerHub.OnDisconnectedAsync`
  (`RunnerHub.cs:36-45`) calls `RunnerDisconnectedAsync` per session from
  `UnregisterAndGetSessions`; `RunnerDisconnectedAsync`
  (`AgentSessionGrain.cs:1311-1319`) guards `Activity != Active → return`, else
  sets `Unknown`. `OnConnectedAsync` (`RunnerHub.cs:21-32`) only registers +
  updates the build hash — no reconcile. ✓
- **Cancel never settles activity.** `AgentSessionCancelRoutes.ExecuteCancelAsync`
  (`AgentSessionCancelRoutes.cs:60-146`) resolves the target, calls
  `EnsureRuntimeSessionPresentAsync` (a presence check, not an activity mutation),
  invokes SignalR `CancelAgentSession`, and returns the reply (surfacing
  `interruptUnconfirmed` at `:143-144`). `CancelHandlerDeps`
  (`cancel-handler.ts:34-38`) has no outbox; `handleCancel` (`:49-118`) returns
  the reply and stops. ✓
- **Recovery requires Idle.** `EnsureSessionIdleForRecovery`
  (`AgentSessionGrain.cs:552-558`) throws on non-Idle, gating
  `RecoverMissingRuntimeSessionAsync` (`:177-190`);
  `RunnerRoutes.cs:344,473` map the throw to `agent_session_recovery_conflict`. ✓
- **`session.activity {idle}` settles Unknown→Idle with no prior-state guard.**
  `ApplyRuntimeEventToDomain` (`:1459-1461`) → `ParseActivity` (`:1482-1488`)
  maps `"idle"→Idle` unconditionally. The `SessionInput`+Unknown drop rule
  (`:839-841`) does not touch `SessionActivity`, so the chosen channel is safe. ✓
- **Binding guard discards superseded facts.** `AppendEventsAsync(...,
  requireCurrentRuntimeBinding:true)` (`:826-837`) rejects any event whose
  `runtimeSessionId` ≠ current binding. `AppendRuntimeEventsAsync` (`:790-791`)
  uses it. ✓
- **`RebindRuntimeSession` requires Idle + CAS.** `AgentSession.Transitions.cs:186`
  throws on non-Idle; `:188` calls `EnsureExpectedRuntimeBinding` (`:213-220`)
  which throws `StaleRuntimeSessionBindingException` on mismatch;
  `"missing-recovery"` is a valid reason (`:191`). ✓
- **Convergence is workflow-only.** `onDispatchReconnected` (`host.ts:350-362`)
  runs `runConvergenceOnce`/`runCleanupOnce`; no AgentSession-binding pass. ✓
- **Durable RunnerId; in-memory index cleared on crash; no runner-scoped query.**
  `AgentSessionRow.RunnerId` (`AgentSessionRow.cs:7`), written at
  `AgentSessionStore.cs:160`. `RunnerConnectionTracker.UnregisterAndGetSessions`
  (`RunnerConnectionTracker.cs:29-36`) clears `_sessions`. No `ListByRunner`
  method exists in `AgentSessionQuerier`/`AgentSessionQuery`/`AgentSessionStore`
  (grep returned nothing). ✓
- **Existence-check drift + typed contract.** `opencode/runtime.ts:134-137`
  (resolve), `:293-296` (followup resolve), `:313-319` (promptAsync), `:369-372`
  (abort) all use `as never`. The typed contract already exists in
  `turn.ts:264-267` (`session.get({ sessionID, directory }, { throwOnError:true})`)
  and the status map read at `turn.ts:347-357`. `readCancelFacts`
  (`command-runtime.ts:139-156`) defaults `stopConfirmed` to `true` (`:145,152`);
  only Pi reports unconfirmed (`pi/runtime.ts:283-311`, `watchPiStop`). ✓
- **Binary probe; only missing authorizes recovery.** `BindingProbeResult`
  (`binding-recovery.ts:15-17`) is `{ok:true} | {ok:false;kind;message}`;
  `:50` present→no candidate; `:51` only `missing-session` proceeds. ✓
- **The durable outbox the design reuses is real.**
  `AgentSessionRuntimeEventOutbox` (`runtime-event-outbox.ts:1-7`) is the
  RunnerHost-owned primitive; the followup handler already records activity
  facts through it (`followup-handler.ts:211` `recordFollowupActivity(...,
  "idle")`, `:215` `..."unknown"`). So D3/D5 extend a proven channel. ✓
- **No conflicting pre-existing symbols.** `ReconcileMissingBinding`,
  `ListByRunnerForReconcile`, `ReconcileAgentSession` do not exist (grep empty). ✓

The architecture (Runner-driven reconcile over the existing convergence loop,
durable non-Idle runner-scoped query, happy-path settle via the existing
`session.activity` channel with zero new grain transition, confirmed-missing
settle+rebind under one CAS, binding-guarded cancel fact, typed-contract
migration) is coherent and reuses existing machinery rather than inventing new
state-transition paths. D4's settle-then-rebind ordering is sound:
`RebindRuntimeSession` (`:186`) checks Idle, and the new grain method settles
Idle first within the same single-threaded activation, so the CAS at `:188`
sees the unchanged old binding.

## Prior findings — both resolved

- **Finding A (input-on-reconnect contradiction) — FIXED.** The prior review
  flagged `runner-reconnect-reconciliation/spec.md` mandating input submission
  on reconnect recovery while design D4 / T-003 forbade it. The current spec
  requirement (`:36`) now reads "A bare reconnect SHALL submit no input — there
  is no triggering input; when a task or Follow-up input is pending, that task
  or Follow-up SHALL submit it exactly once ... and it SHALL NEVER be replayed
  by reconnect or retry," with a matching scenario (`:44-49`). This is now
  fully consistent with D4 and T-003 ("No input is submitted on bare
  reconnect"). The general `runtime-binding-recovery` "submit the triggering
  input exactly once" (`:53`) is consistent because "the triggering input"
  presupposes a triggering task/follow-up, which the reconnect spec carves out.
- **Finding B (proposal Impact staleness) — FIXED.** The current proposal Impact
  states "unchanged on reconnect — `OnConnectedAsync` is not modified.
  Reconciliation is Runner-driven" and "the cancel route remains a pure SignalR
  invoke ... it does not record the activity fact," matching D1/D5 exactly. The
  stale hub-orchestrated / route-records-fact wording the prior review cited is
  gone.

## Observations (non-blocking)

These do not prevent a correct build — the tasks are the build contract and are
unambiguous — but are recorded so a cleanup task may tighten the artifacts.

### O1 — reconnect spec attributes enumeration/reconciliation to "the Server" and says "every"; design is Runner-driven over non-Idle sessions

`runner-reconnect-reconciliation/spec.md:3` requirement 1 and its first scenario
(`:8`) say "the Server SHALL enumerate **every** AgentSession bound to that
Runner and reconcile each one ... using the owning Runner's ... existence
check." The chosen mechanism (D1/D2, T-003) is Runner-driven — the Runner
**pulls** the list via `GET .../reconcile`, probes locally, and reports facts
back — and the query is filtered to `Activity != Idle` (D2 rationale: Idle
sessions are already settled). 

This is a wording/quantifier mismatch, not a behavioral one. The behavioral
outcome is identical: every bound session ends up correctly reconciled (Idle
ones are already in their target state; non-Idle ones are probed and settled),
and the spec scenarios are outcome-based and satisfiable by the Runner-driven
mechanism. A literal reading of "the Server SHALL enumerate ... using the owning
Runner's check" actually describes the Server-orchestrated alternative D1
explicitly rejected (D1 alt-a), but the tasks steer the builder to the correct
Runner-driven path, so no wrong work results. Recommend rephrasing the
requirement/scenario to be mechanism-agnostic ("reconciliation SHALL cover each
... ") or to name the Runner as the reconciler, and to acknowledge the non-Idle
filter.

### O2 — no dedicated scenario for "uncertain input acceptance"

The issue's enumerated non-recovery set includes "uncertain input acceptance,"
and every sibling condition (timeout, transport failure, unavailable runtime,
corrupt response, `active`, unresolved `unknown`) has a dedicated scenario in
`runtime-binding-recovery/spec.md:73-111` — except this one. It is not a
behavioral gap: this issue does not change the input-submission path, and
"uncertain input acceptance → no replay" is already guaranteed by the existing
outbox `matching-receipt` policy (the head is retained until a matching receipt
confirms acceptance) plus the "unresolved unknown blocks recovery" scenario.
Adding an explicit scenario would make the coverage symmetric with the issue
wording.

### O3 — concurrent task + reconcile can orphan a candidate Session

If a task's `resolveOrRecoverBinding` and a reconnect reconcile pass both probe
the same stale binding as missing before either rebinds, each creates a local
candidate; the grain CAS (`EnsureExpectedRuntimeBinding`) then lets only one
rebind succeed, leaving the other candidate unbound. This is a pre-existing
property of `resolveOrRecoverBinding` (not introduced here), does not violate
binding correctness, and the issue's "no multiple empty candidates" criterion is
about a *single* AgentSession accumulating rebound candidates — which the CAS
prevents (the second attempt probes the new binding → present → no creation).
The design's "either ordering is safe" claim is accurate for the binding; the
orphaned-candidate edge is a minor resource observation, not a correctness risk.

## Coverage

All eight issue acceptance criteria map onto the three specs and three tasks:
reconnect-keeps-binding (D3/T-003), cancel-settles-idle (D5/T-002),
never-missing/replaced (D6/T-001 + T-003), confirmed-missing one-candidate +
input-once (D4/T-003), non-recovery-failure-set (T-003 + existing infra),
superseded-binding-invariant (D7, all specs), no-multiple-candidates (D4 CAS),
and the #489 auto-recovery scenario (Migration §3). Task ordering (T-003
depends on T-001's enriched active-turn probe; T-002 independent) is correct.

<promise>PASS</promise>
