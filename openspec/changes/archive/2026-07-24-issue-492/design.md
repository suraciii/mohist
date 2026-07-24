## Context

AgentSession is the stable logical conversation; the Runtime Session (OpenCode/Pi) is a mutable physical facet owned by the Runner. Issue #484 established the authoritative activity model (`Idle`/`Active`/`Unknown`), the binding CAS, and the `RunnerDisconnectedAsync` watchdog that flips `Active → Unknown` on SignalR disconnect. **#484 added the disconnect half but never the reconnect counterpart** — this is the gap #492 closes.

Current state (key facts, with sources):

- **Disconnect is one-sided.** `RunnerHub.OnDisconnectedAsync` (`RunnerHub.cs:36-45`) fires `AgentSessionGrain.RunnerDisconnectedAsync()` (`AgentSessionGrain.cs:1311-1319`) per registered session, flipping `Active → Unknown`. There is **no** `OnConnectedAsync` reconciliation: `RunnerHub.cs:21-32` only registers the connection and updates the build hash. Nothing converges `Unknown → Idle` on reconnect.
- **Cancel never settles activity.** `AgentSessionCancelRoutes.cs:60-146` is a pure SignalR invocation to the Runner; it echoes the Runner's reported state but calls no grain method that mutates `Activity`. `cancel-handler.ts:49-118` returns the reply and stops — its `CancelHandlerDeps` (`:34-38`) has **no** `AgentSessionRuntimeEventOutbox`, so a confirmed stop is never written back. A session stuck `Unknown` after a Runner restart stays stuck until an operator injects a runtime event.
- **Recovery requires `Idle`.** `EnsureSessionIdleForRecovery` (`AgentSessionGrain.cs:552-558`) gates `RecoverMissingRuntimeSessionAsync` (`:177-190`), Compact, and Reset on `Activity == Idle`; `Unknown` throws `InvalidOperationException` → HTTP 409 `agent_session_recovery_conflict` (`RunnerRoutes.cs:344,473`). So a `Unknown` session cannot be recovered and a still-existing physical Session is not re-validated.
- **`Unknown → Idle` already settles via `session.activity`.** `ApplyRuntimeEventToDomain` (`AgentSessionGrain.cs:1459-1461`) routes a `session.activity` event through `ParseActivity` (`:1482-1488`); `"idle"` → `Idle` with **no** guard against coming from `Unknown`. The binding guard is automatic: the runtime-events channel calls `AppendEventsAsync(..., requireCurrentRuntimeBinding: true)` (`:790,826-837`), discarding facts whose `runtimeSessionId` is not the current binding.
- **The reconnect convergence loop exists, but only for WorkflowRuns.** `host.onDispatchReconnected` (`host.ts:350-362`) runs `runConvergenceOnce`/`runCleanupOnce` from `cleanup-convergence.ts`, which reconciles WorkflowRun terminal statuses against the workspace registry. There is **no** AgentSession-binding equivalent.
- **No durable "list AgentSessions by Runner" query.** `AgentSessionRow.RunnerId` is a persisted column (`AgentSessionRow.cs:7`, written at `AgentSessionStore.cs:160`), but `AgentSessionQuery`/`AgentSessionQuerier` expose no runner-scoped filter. The only runner→sessions index is `RunnerConnectionTracker._sessions`, which is in-memory, process-local, and **empty after a Runner crash** (disconnect cleared it).
- **Existence checks are drifted.** `opencode/runtime.ts:134-137` probes via `session.get({ path:{id}, query:{directory} } as never)` + an untyped `as { data?: { id? } }` read. The turn path (`turn.ts:264-267`) already uses the typed contract `session.get({ sessionID, directory }, { throwOnError: true })`. Same `as never` drift in the followup resolve (`:293-296`), followup promptAsync (`:313-319`), and cancel abort (`:369-372`).

Stakeholders: Workflow (TaskRun retry), Agent (AgentJob), Session (grain/querier/API), Runner (host/adapters/outbox). Per `AGENTS.md` the project is in active development with no version-compatibility constraint.

## Goals / Non-Goals

**Goals:**
- After Runner reconnect, a still-existing physical Session with no active turn keeps its binding and settles `Unknown`/`Active → Idle`; the next task continues with the existing context, no operator repair.
- A confirmed Cancel settles activity to `Idle` through the normal API/CLI (binding-guarded, no operator-written runtime event).
- A still-queryable physical Session is never classified missing or replaced across task start, retry, Follow-up, Compact, Cancel, or reconnect.
- Only a confirmed-missing result from the owning Runner authorizes recovery; on reconnect it settles idle (authoritative missing evidence) + rebinds at most one candidate.
- Every non-recovery condition (timeout, transport failure, unavailable runtime, corrupt response, uncertain input acceptance, `active`, unresolved `unknown`) preserves the binding and never replays input.
- All physical-Session existence checks route through the typed, type-checked OpenCode SDK request contract.

**Non-Goals** (from issue):
- Diagnose/change the Runner memory limit, OpenCode event subscription scope/payload budget, or OpenCode DB retention.
- Copy or replay context from a confirmed-missing Session into its replacement.
- Add physical Runtime Session history/lineage to AgentSession.
- Change Workflow retry/rerun/recovery-budget or task-result semantics.
- Extend the runtime metrics/status surface tracked by #470.

## Decisions

### D1. Reconcile is Runner-driven through the existing reconnect convergence loop

Add an AgentSession-binding reconciliation pass to `onDispatchReconnected` (`host.ts:350-362`), parallel to the WorkflowRun `ConvergenceBackstop` (`cleanup-convergence.ts:47-85`). The Runner pulls the list of non-`Idle` sessions bound to it, probes each via the local runtime SDK, and reports authoritative facts back through **existing** channels.

**Rationale.** The runtime SDK lives on the Runner (`design/runtimes/README.md`); the Server never talks to the runtime directly ("Runner 只报告物理事实；Session 是 binding 与 activity 的状态裁判"). The Runner already resolves/recreates inline in `executor.ts`/`agent-job-executor.ts`/`followup-handler.ts`, already reports `session.activity` facts via the durable outbox, and already runs a convergence loop on reconnect. Reconnect reconciliation is the same routine applied to a pulled list rather than a task input — it reuses the most machinery and adds no Server→runtime coupling.

**Alternatives.** (a) Server/hub-orchestrated: `OnConnectedAsync` enumerates sessions and probes the Runner via a new SignalR `ReconcileAgentSession` contract, then calls the grain. Rejected: it forces a new Server→Runner probe SignalR contract and Server-held orchestration, and the missing-case candidate creation would need a second round trip. The Runner already holds the SDK and the convergence trigger. (b) Grain calls SignalR (`IHubContext`) to probe — rejected: couples grains to the transport, violating the existing pattern where grains are transport-free referees.

### D2. Reconcile list is a durable query over `AgentSessionRow.RunnerId`

Add `IAgentSessionStore.ListByRunnerForReconcileAsync(runnerId)` and expose it via `GET /api/runner/{runnerId}/agent-sessions/reconcile`, returning sessions bound to `runnerId` whose `Activity != Idle`, projected to `{ sessionId, runtime, runtimeSessionId, workDir }`. The query filters on the durable `AgentSessionRow.RunnerId` column (and `Activity` read from the state projection), **not** the in-memory `RunnerConnectionTracker._sessions`.

**Rationale.** After a Runner crash, `OnDisconnectedAsync` → `UnregisterAndGetSessions` already cleared the in-memory session set (`RunnerConnectionTracker.cs:29-36`), so the in-memory index is empty precisely when reconcile is needed. Only the durable `RunnerId` column survives a Runner restart. Reconciling only non-`Idle` sessions bounds the probe set (`Idle` sessions are already settled and need no re-validation), keeping cost proportional to currently-relevant work, not history (`design/architecture.md:88`).

**Alternatives.** (a) Reconcile every session bound to the Runner regardless of activity — rejected: unnecessary probes on healthy `Idle` sessions, unbounded as sessions accumulate. (b) Push the list from the hub via SignalR on connect — rejected: couples the hub to Runner reconcile logic; the Runner already pulls workflow statuses in its convergence loop, so a pull is consistent.

### D3. The happy path settles `Unknown → Idle` through the EXISTING `session.activity` channel — no new grain transition

When reconcile probes a binding and the owning Runner confirms the physical Session **exists and has no active turn**, the Runner enqueues a binding-guarded `session.activity { idle }` fact through `AgentSessionRuntimeEventOutbox` (the same channel followup completion uses today). The grain's existing `ApplyRuntimeEventToDomain` → `ParseActivity("idle")` → `SetActivity(Idle)` (`AgentSessionGrain.cs:1459-1461,1482-1488`) settles `Unknown → Idle` with **no** guard against the prior `Unknown` state, and `AppendEventsAsync(..., requireCurrentRuntimeBinding: true)` (`:826-837`) automatically discards the fact if the binding was superseded.

**Rationale.** This is the sanctioned `unknown + authoritative runtime evidence → idle` transition (`agent-session-activity` spec). It already exists in the grain; #489 was broken only because nobody produced the authoritative evidence on reconnect. Reusing the durable outbox gives at-least-once delivery, ordering, and the superseded-binding discard for free. The main fix for #489 therefore adds **zero** new server-side transition logic — only the Runner-side reconcile that emits the fact.

**Alternatives.** (a) A new dedicated grain `SettleIdleOnReconnectAsync` method — rejected: duplicates a transition that `session.activity { idle }` already performs, and bypasses the durable outbox ordering/binding-guard that the existing channel provides. (b) Treat `unknown` as `idle` directly in the idle-gate — explicitly rejected by the issue ("`unknown` must never be treated as a safe `idle`"); only authoritative evidence about the **current binding** may settle it.

### D4. Confirmed-missing on reconnect settles idle + rebinds atomically; no input on bare reconnect

When reconcile probes and the owning Runner confirms the physical Session is **missing**, the Runner creates at most one empty candidate Session locally (reusing `createEmptySession`), then calls a new `POST .../reconcile-missing` endpoint backed by a new `IAgentSessionGrain.ReconcileMissingBindingAsync(expected, replacement)`. That grain method, under the expected-binding CAS: settles `Activity = Idle` (a confirmed-missing result is authoritative evidence that the binding has no active turn — the Session is gone), then calls the existing `RebindRuntimeSession(expected, replacement, "missing-recovery")` (`AgentSession.Transitions.cs:180-208`), atomically. **No input is submitted** on a bare reconnect (there is no triggering input); the next task/follow-up submits input exactly once against the confirmed replacement via the unchanged `resolveOrRecoverBinding` path.

**Rationale.** Recovery's `Idle` requirement is what blocked #489 (`agent_session_recovery_conflict`). The resolution is that a confirmed-missing fact from the owning Runner IS the authoritative binding evidence that authorizes the `unknown → idle` settle — so settle and rebind in one grain call, under one CAS, with no window for a concurrent task to interpose. Reusing `RebindRuntimeSession` keeps a single binding-replacement routine (D4 of #484's design). Reconnect joins task start and idle-Follow-up as a sanctioned recovery trigger (proposal: "reconnect joins task and idle-Follow-up input as sanctioned recovery triggers"), but unlike those it carries no input — the "submit input exactly once" rule applies when a triggering input exists. At-most-one-candidate is guaranteed: after rebind the binding is the replacement, so a flapping reconnect or a concurrent task probes the **new** binding → present → no second candidate (`binding-recovery.ts:50`).

**Alternatives.** (a) Defer candidate creation to the next task — reconcile only settles `Idle`, the next task's `resolveOrRecoverBinding` creates the candidate. Rejected: leaves a known-bad binding live longer than necessary and makes "reconnect triggers recovery" (the spec scenario) indirect; the spec's "create at most one candidate and confirm the replacement" is satisfied more directly by reconcile-driven rebind. (b) Reuse the existing `recover-missing` HTTP route for reconnect — rejected: it requires `Idle` and the session is `Unknown`; the reconcile path must settle-idle-first, which is a distinct intent worth its own endpoint/grain method rather than overloading `recover-missing` with a mode flag.

### D5. Cancel settles activity through the binding-guarded runtime-events outbox

Add `AgentSessionRuntimeEventOutbox` to `CancelHandlerDeps` (`cancel-handler.ts:34-38`). After `callCancel`:
- confirmed stop (`facts.cancelled && facts.stopConfirmed`) → enqueue binding-guarded `session.activity { idle }` (the existing `ParseActivity("idle") → Idle` path settles `Active`/`Unknown → Idle`);
- unconfirmed stop (`facts.cancelled && !facts.stopConfirmed`) → enqueue binding-guarded `session.activity { unknown }` (honest uncertainty — the spec forbids reporting an unconfirmed stop as `idle`), and the SignalR reply keeps surfacing `interruptUnconfirmed: true`.

Both carry the current `runtimeSessionId`, so `AppendEventsAsync(..., requireCurrentRuntimeBinding: true)` discards the fact if the binding was superseded (e.g. a concurrent Reset replaced it). OpenCode facts have no `stopConfirmed` field today (`command-runtime.ts:readCancelFacts` defaults it to `true`), so OpenCode cancels settle `Idle`; only Pi can produce a real unconfirmed stop (`pi/runtime.ts:283-311`, `watchPiStop`).

**Rationale.** Cancel currently reports state honestly to the caller but never to the aggregate — the exact bug. Writing the fact through the same durable outbox the followup path uses keeps one fact-reporting channel, gives at-least-once delivery, and makes the superseded-binding guard automatic. The binding-guarded settle is the spec's "stop fact SHALL apply only to the current binding."

**Alternatives.** (a) A synchronous grain `CancelAsync(sessionId)` invoked from `AgentSessionCancelRoutes` — rejected: would settle activity on the RPC return path, which is unreliable (the route returns whatever the Runner honestly reported, including `interruptUnconfirmed`); durable settlement must come from the Runner's confirmed fact over the outbox, not from the request/response. (b) A cancel-specific transcript event — rejected: `session.activity` is already the continuous-state signal (#484 D6); a second event duplicates it.

### D6. All existence checks route through the typed OpenCode SDK contract; probe result carries active-turn

Replace every `as never` OpenCode call with the typed contract `turn.ts` already uses, and enrich the probe to report whether the Session has an active turn:
- `opencode/runtime.ts:134-137` (resolve) and `:293-296` (followup resolve) → `session.get({ sessionID, directory }, { throwOnError: true })`; after confirming existence, capture the active-turn snapshot via `session.status({ directory }, { throwOnError: true })` (the per-Session status map `turn.ts:347-357,685-696` already reads).
- `opencode/runtime.ts:313-319` (followup promptAsync) and `:369-372` (cancel abort) → the same typed `session.promptAsync`/`session.abort` shapes `turn.ts` uses.
- `pi/runtime.ts:83-96` (resolve) → surface `session.isStreaming` (already exposed on the cached session handle, used by `pi/runtime.ts:293-310`) as the active-turn signal in the resolve result.
- `binding-recovery.ts:15-17` → extend `BindingProbeResult` from binary `{ ok } | { ok:false, kind }` to carry `activeTurn: boolean` on the present branch, so reconcile distinguishes present+idle from present+active. Only `kind === "missing-session"` still authorizes recovery; `present` (with any activeTurn) preserves the binding.

**Rationale.** The spec requires existence checks be type-checked so SDK DTO drift cannot hide a misclassification. The typed contract is already proven in `turn.ts`; the `runtime.ts` calls predate it. The active-turn snapshot is what lets reconcile settle `idle` only when there is genuinely no active turn (and keep `active` honest when a turn survived a transient reconnect). Pi's active-turn is a synchronous `isStreaming` getter — no SDK drift there, just needs surfacing.

**Alternatives.** (a) Keep the untyped call but add a runtime assertion — rejected: an assertion only fires in tests; the type system must enforce it at compile time. (b) Add active-turn only to a new reconcile-specific probe, leaving `resolveSession` binary — rejected: reconcile and task/followup resolve ask the same physical question; one typed probe serves all callers and prevents the binary result from ever classifying an `active` Session as recoverable.

### D7. Superseded-binding safety is automatic; no new guard code

Every settle/recover path lands on existing CAS or binding-guarded channels:
- runtime-events outbox facts carry `runtimeSessionId` and are discarded by `AppendEventsAsync(..., requireCurrentRuntimeBinding: true)` when the binding changed;
- `ReconcileMissingBindingAsync` and `RebindRuntimeSession` call `EnsureExpectedRuntimeBinding` (`AgentSession.Transitions.cs:213-220`) → `StaleRuntimeSessionBindingException` → HTTP 409 `stale_binding`.

So a fact or recovery request for an old binding cannot change the current binding, activity, transcript, or usage.

**Rationale.** The invariant ("facts for an old binding cannot change current state") is already enforced by #484's CAS; #492 only adds new producers of facts (reconcile, cancel) that flow through those guarded channels. No bespoke staleness logic is needed.

## Risks / Trade-offs

- **[Reconcile storm / flapping Runner] →** Reconcile only touches non-`Idle` sessions (D2), is CAS-idempotent (settling `Idle` when already `Idle` is a no-op; recovery CAS rejects a second candidate), and bounded per-session. A subsequent reconnect re-attempts reconcile for any session still non-`Idle`. No wall-clock timing is used (per `design/testing.md`).
- **[Concurrent task vs reconcile] →** Both land on the grain's single-threaded CAS. If reconcile settles idle + rebinds first, the task's `resolveOrRecoverBinding` sees the new binding → present → uses it (no second candidate, input submitted once). If the task resolves first, reconcile probes the same binding → present → no-op. Either ordering is safe.
- **[Server restart (not Runner)] →** `Activity` is persisted in the grain state blob and reloads on activation, so a Server restart does not flip activity. Reconcile on the next Runner reconnect finds `Idle` sessions (skipped) or `Unknown` sessions (settled). No backfill needed.
- **[Confirmed-missing candidate created when the Session was about to be reused] →** Gated on a confirmed-missing result from the **owning** Runner only (D6 typed check); `present`/`active`/`unavailable` never authorize recovery. Residual risk is a TOCTOU between probe and create, absorbed by the CAS rebind.
- **[Cancel outbox write races a concurrent Reset] →** The binding-guarded discard (D7) drops the stale `idle` fact; the Reset's rebind wins. No double-settle.
- **[OpenCode `stopConfirmed` always `true`] →** OpenCode's abort is treated as authoritative (`command-runtime.ts`); an OpenCode cancel that did not actually stop will settle `Idle` optimistically. This matches current behavior (the abort is best-effort) and is no worse than today; Pi is the only runtime that can honestly report unconfirmed.

## Migration Plan

Active development, single deployment, no version-compatibility constraint — migration is code-driven:

1. **Grain state.** No schema change. `Activity` already persists (#484). Reconcile/cancel only produce facts through existing channels. `AgentSessionRow.RunnerId` already exists.
2. **Code.** Add: durable `ListByRunnerForReconcileAsync` + `GET .../reconcile` endpoint (D2); `ReconcileMissingBindingAsync` grain method + `POST .../reconcile-missing` endpoint (D4); Runner `binding-convergence` pass in `onDispatchReconnected` (D1/D3); outbox field in `CancelHandlerDeps` + the cancel fact write (D5); typed OpenCode calls + active-turn snapshot + enriched `BindingProbeResult` (D6).
3. **Existing sessions.** A session stuck `Unknown` from a prior Runner restart becomes reconcilable on the next Runner reconnect — it auto-settles to `Idle` (or rebinds if confirmed-missing) with no operator action. This is the #489 scenario recovering automatically.
4. **Rollback.** Revert the code. Reconcile simply stops running; sessions already settled by the change remain in their (correct) state. No forward-only data format is introduced.

## Open Questions

- **Reconcile scope: `Unknown` only, or `Unknown` + `Active`?** Default: reconcile both. After a graceful Runner reconnect without a detected disconnect, a Session may still be `Active`; probing it keeps activity honest (settle `Idle` if the turn ended, keep `Active` if still running). The cost is one extra probe per `Active` Session, bounded and small.
- **present + active fact on reconcile: write `session.activity { active }` or skip?** Default: write it, so a `Unknown` Session whose turn genuinely survived a transient reconnect is honestly promoted to `Active` rather than left `Unknown`. Skippable if deemed noisy, with no correctness impact (the turn's natural completion will later report `Idle`).
