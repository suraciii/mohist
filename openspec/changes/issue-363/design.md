## Context

Issue #362 landed the self-driving durable event dispatcher (`design/eventbus.md`). Events are now delivered at-least-once **out-of-stack**: a cluster-singleton reminder queries undispatched rows, fans out to `ICloudEventHandler`s, retries per handler with backoff, and dead-letters on exhaustion. This removes the precondition that justified three categories of compensating patches:

- **`[Reentrant]` on authority grains** (`WorkflowGrain` `Workflow/Grains/WorkflowGrain.cs:22`, `RunnerGrain` `Runner/Grains/RunnerGrain.cs:33`). The mark existed so a synchronous in-stack handler could call back into the publishing grain without deadlocking. Delivery is now off-stack; no handler ever re-enters the publishing grain's turn.
- **`RunnerGrain._worksStateWriteGate`** (`RunnerGrain.cs:43`), a `SemaphoreSlim` serializing `WriteStateAsync`. It existed *because* reentrancy allowed turns to interleave; with turn serialization restored it is redundant.
- **Handler try-catch swallow** in `AgentSubscriptionDispatchHandler.HandleAsync` (`Events/Subscriptions/AgentSubscriptionDispatchHandler.cs:79-92`) and `HermesIssueNotificationHandler.HandleAsync` (`Events/Subscriptions/HermesIssueNotificationHandler.cs:50-74`). The swallow predates the dispatcher's unified retry/DLQ path; it now hides failures the dispatcher is built to recover from. (`RunnerWorkflowTerminalStatusHandler`'s detach was already removed in #362; remaining prose is stale.)
- **`EpicReconciliationService`** (`Events/Hosting/EpicReconciliationService.cs`) — a 10-minute `BackgroundService` sweep that re-invoked `ReconcileAfterTerminalAsync` on idle+running epics to cover missed `com.mohist.issue.completed/cancelled` events. Durable delivery makes the miss impossible to silently drop.

Separately, `ReconcileAfterTerminalAsync` is a misnomer: it recomputes epic progress from member state (MarkDone detection + `TryStartNext` serial advance), it does not "reconcile a discrepancy". The name collides with the workflow-scheduling domain's distinct `DispatchService` reconcile (`design/workflow/scheduling.md`, epic #44). The rename is part of this change.

Current state:
- `WorkflowGrain` declares `[Reentrant]` and exposes `IGrainFactory` via `IWorkflowGrainContext.Grains` but does **not** call back into itself via the factory (verified: no `GetGrain<IWorkflowGrain>` self-reference in the `Workflow` slice). Intra-grain helper calls (`WorkflowStageLockCoordinator`, `WorkflowStageInitializer`, `WorkflowWorkLifecycle`) are direct method calls, not scheduler-mediated.
- `RunnerGrain` declares `[Reentrant]`, owns `_worksStateWriteGate`, and calls `GrainFactory.GetGrain<IWorkflowGrain>(...)` (cross-grain, not self). It also has a poll-admission lease: `TryBeginPollAsync` holds `_pollAdmissionGate` across later grain calls to `TouchPresenceAsync`, `GetInfoAsync`, `ReconcileAgentJobsAsync`, and finally `EndPollAsync`, while `UnregisterAsync` and `UpdateAsync` may wait for that lease. Without broad reentrancy, either lifecycle call can occupy the turn before the poll reaches its next call; marking only `EndPollAsync` interleavable cannot help. Runner also awaits `AgentJobGrain` during reconciliation, result reporting, and closeout while AgentJob assignment can await `RunnerGrain.AssignAgentJobAsync`, creating a separate reciprocal progress dependency currently masked by broad Runner reentrancy.
- `EpicGrain.ReconcileAfterTerminalAsync` (`Epic/Grains/EpicGrain.cs:653`) + `ReconcileAfterTerminalInternalAsync` (`:683`) have three required semantic triggers: completed and cancelled handlers share one dispatcher call to the grain entry, while `EpicGrain.ResumeAsync` (`:524`) calls the internal method directly. The hosted sweep is an additional current caller of the public grain entry.
- `HermesIssueNotificationHandler` uses a two-phase model: `HandleAsync` does synchronous setup (options/type resolution + enqueue) then returns; `_dispatcher.Dispatch` (`BackgroundHermesIssueNotificationDispatcher`) runs `DeliverAsync` on a background `Task.Run`. The background delivery is the best-effort notification channel (`design/architecture.md` "Events: two channels": domain reaction = durable, UI push = best-effort).
- `EpicReconciliationService` scans every idle and running epic. In addition to retrying missed terminal recomputes, it can observe a startable issue linked to an already-running epic, readiness changes that do not emit completed/cancelled events, terminal state that predates membership, and idle epics whose members are already complete.

Stakeholders: Server slice owners (Workflow, Runner, Events, Epic). No web/CLI/runner/API-contract stakeholders — pure server-internal refactor.

## Goals / Non-Goals

**Goals:**
- Restore Orleans turn-based serialization for authority-state mutations in `WorkflowGrain` and `RunnerGrain`; remove broad `[Reentrant]` and the manual persistent-state write gate while preserving poll, lifecycle, and Runner↔AgentJob progress.
- Collapse error handling for the covered durable processing stages to the dispatcher's single aggregation point; preserve intentionally best-effort UI-delivery error handling.
- Remove the poll-driven epic terminal-recompute sweep; make durable event delivery reliable for member terminal events while preserving or explicitly changing the other readiness behavior the sweep currently supplies (manual `ResumeAsync` re-evaluation remains).
- Rename the epic progress-recompute chain from "reconcile" to "recompute progress" across the contract, implementation, dispatcher, handlers, docs, and tests — while preserving the grain method and its three semantic triggers.
- Add concurrency characteristic tests proving the reentrancy removal does not introduce torn state.

**Non-Goals:**
- Rewrite the workflow state machine or change cross-aggregate event→command orchestration semantics.
- Touch workflow-scheduling reconcile (`DispatchService` / `design/workflow/scheduling.md` / epic #44).
- Change `AutoMarkDoneIfReadyAsync` (a separate readiness entry, not in the rename scope).
- Make Hermes notification delivery durable (it is intentionally best-effort; only its setup phase propagates to the dispatcher).
- Any web / CLI / runner / HTTP contract change, new external dependency, or persistence schema change.

## Decisions

### D1 — Remove broad `[Reentrant]` from WorkflowGrain; defer RunnerGrain to a follow-up prerequisite

Turn serialization is the intended guarantee for authority-state mutations (`design/architecture.md`: "Authority grains: no `[Reentrant]`"). With delivery off-stack (#362), the original synchronous-handler callback rationale is gone. `WorkflowGrain` can restore that model directly; `RunnerGrain` cannot do so until the unrelated progress dependencies below stop relying on broad interleaving.

`WorkflowGrain` has no scheduler-mediated self-call or later-call release dependency, so its broad attribute can be removed directly.

`RunnerGrain` is blocked on two independent progress problems:

1. The poll-admission lease spans multiple separate grain calls. `UnregisterAsync` or `UpdateAsync` can occupy the serialized turn while waiting for `_pollAdmissionGate`, preventing the admitted poll from invoking `TouchPresenceAsync`, `GetInfoAsync`, or `ReconcileAgentJobsAsync` and therefore from ever reaching `EndPollAsync`. Marking only `EndPollAsync` `[AlwaysInterleave]` is not sufficient. Marking the intervening methods interleavable is also invalid because presence refresh and AgentJob reconciliation mutate authority state.
2. AgentJob assignment can await `RunnerGrain.AssignAgentJobAsync` while Runner reconciliation, result reporting, or closeout awaits that same `AgentJobGrain`. Broad Runner reentrancy currently permits the inbound assignment to make progress. Turn serialization requires a handoff protocol that does not hold one authority turn while awaiting a reciprocal call.

This issue explicitly revises the scope: RunnerGrain reentrancy removal is deferred to a follow-up prerequisite that resolves both progress problems without modifying `DispatchService`. The poll sequence is implemented by `DispatchService`, while this issue's non-goals explicitly prohibit touching workflow scheduling reconcile. The AgentJob handoff also requires an ownership/acknowledgement decision. Both redesigns are assigned to the follow-up prerequisite, not this issue. WorkflowGrain reentrancy removal proceeds directly in this issue.

**Alternatives:**
- *Keep `[Reentrant]` + explicit locking.* Re-adds the complexity being removed and hides the real guarantee behind ad-hoc locks. Rejected.
- *Keep `[Reentrant]` as a no-op for future flexibility.* The whole point of the issue is that it is not a no-op — it re-enables interleaving. Remove it.

**Scope decision:** RunnerGrain reentrancy removal is deferred to a follow-up prerequisite. WorkflowGrain reentrancy removal proceeds in this issue. Audit `WorkflowGrain` for any `await` path that resolves a call back into the **same grain activation** (verified: none exists). Cover the restored turn model with real Orleans characteristic tests for `WorkflowGrain`.

### D2 — Remove `RunnerGrain._worksStateWriteGate` (deferred to follow-up prerequisite)

The gate serialized `WriteStateAsync` only because reentrancy let turns interleave. After D1's Runner blockers are resolved and one-turn-at-a-time execution is restored, two `PersistAsync` calls on the same activation cannot overlap. The persistence contract is unchanged: writes remain durable and a failed `WriteStateAsync` still propagates the exception to the caller. All 11 `PersistAsync` call sites (`RunnerGrain.cs:116,145,167,194,273,341,355,382,484,645,706`) remain semantically unchanged.

**Deferred:** This decision is assigned to the follow-up prerequisite that resolves D1's Runner progress problems. `RunnerGrain._worksStateWriteGate` remains in this issue.

**Alternatives:**
- *Keep the gate as defense-in-depth.* Redundant with turn serialization, adds contention, and obscures the real guarantee. Rejected.
- *Keep the gate but document it.* Violates the "code says what it means" principle the issue is enforcing. Rejected.

### D3 — Remove the try-catch swallow in `AgentSubscriptionDispatchHandler.HandleAsync`; keep envelope-level no-op returns

Drop the `catch (Exception ex) { _log.LogWarning(...); }` wrapper (`AgentSubscriptionDispatchHandler.cs:79-92`). Let the `OperationCanceledException`-when-cancelled rethrow remain (cooperative cancellation, per spec). The envelope-level skips (no `projectid`, no active matched subscription, empty rendered prompt) stay as `return` without throwing — those are valid no-ops, not failures. The inner `DispatchAsync` body is unchanged.

**Alternatives:**
- *Catch + log + a structured "delivery failed" signal.* Re-invents what the dispatcher already does (retry + DLQ). Rejected.

### D4 — Remove the setup try-catch in `HermesIssueNotificationHandler.HandleAsync`; keep the background `DeliverAsync` try-catch and the two-phase model

The handler is two-phase: synchronous **setup** (options/type resolution + `_dispatcher.Dispatch` enqueue) returns `Task.CompletedTask`; **delivery** runs in the background via `BackgroundHermesIssueNotificationDispatcher.Task.Run`. Per `design/architecture.md`'s two-channel split, notification delivery is best-effort UI push, not a durable domain reaction. Therefore:

- Remove the outer try-catch in `HandleAsync` (`HermesIssueNotificationHandler.cs:50-74`). **Setup** failures (options resolution, type resolution, dispatch enqueue) now propagate to the durable dispatcher, which retries/dead-letters. The disabled-notification and unconfigured-webhook early returns stay as no-ops.
- Keep `DeliverAsync`'s own try-catch (`:79-101`) and `BackgroundHermesIssueNotificationDispatcher`'s `Task.Run` catch. They are the best-effort channel's own error handling; delivery is intentionally off the durable dispatch stack.

The spec's "handler SHALL NOT detach its side effect to hide a failure" applies to the handler's `HandleAsync` return contract — which is preserved: setup failures propagate, only the best-effort *delivery* is detached by design. This is not a patch; it is the two-channel architecture.

**Alternatives:**
- *Make notification delivery durable.* Wrong channel; out of scope; would couple a best-effort UI push to the durable domain-reaction contract. Rejected.
- *Remove the `DeliverAsync` try-catch too.* Then background failures surface only in `BackgroundHermesIssueNotificationDispatcher`'s generic catch, losing the cancellation-vs-failure log distinction. Rejected — the background catch is the channel's own concern, not the dispatcher's.

### D5 — Delete `EpicReconciliationService` + `EpicReconciliationOptions` + the `AddHostedService<EpicReconciliationService>()` registration

Durable delivery of `com.mohist.issue.completed/cancelled` becomes the reliable automatic trigger for recompute in response to member terminal events. A dead-lettered event stays in the DLQ for operator re-delivery — not silently dropped — so the sweep's missed-event rationale disappears. Delete `Events/Hosting/EpicReconciliationService.cs` (contains both the `BackgroundService` and `EpicReconciliationOptions`) and remove the `services.AddHostedService<EpicReconciliationService>()` line (`MohistServiceRegistration.cs:98`). Keep `using Mohist.Server.Events.Hosting;` because `DispatcherActivationService` remains registered from that namespace.

**Resolved error contract:** `TryStartNextAsync` currently catches `IIssueGrain.StartWorkAsync` failures and returns success. Add an explicit private start-failure mode with `PreserveRunning` and `Propagate`. `RecomputeProgressAsync` calls `RecomputeProgressInternalAsync` with `Propagate`, so terminal-event failures escape to the durable dispatcher. `StartAsync`, link operations, and `ResumeAsync` use `PreserveRunning`, retaining their current running-but-idle behavior. Implement the catch as a filtered catch for `PreserveRunning`; `Propagate` failures are not logged-and-swallowed locally. Tests cover both terminal-event propagation and the unchanged command paths.

**Link-time recompute trigger (resolved):** the service is not only a missed-event retry. Its scan can make progress after a startable issue is linked to a running epic, and when an issue was already terminal before it was linked. It also marks idle epics done when all members are complete at link time. These behaviors are preserved by adding a `RecomputeProgressInternalAsync` call after non-wake links to non-terminal epics in `LinkIssueAsync` and `LinkIssuesAsync`. Draft/prerequisite readiness is covered by durable delivery of the prerequisite's `completed` event. The sweep is deleted after the link-time trigger is in place. No cross-aggregate event->command semantics are changed.

**Alternatives:**
- *Keep the sweep at a slower cadence as defense-in-depth.* Re-introduces the patch the issue removes, conflates a poll-driven scan with event-driven recompute, and adds load for no durable benefit. Rejected.
- *Keep `EpicReconciliationOptions` inert.* Dead config surface. Rejected.

### D6 — Rename the epic progress-recompute chain; keep the grain method and all three required semantic triggers

The method carries real domain logic (MarkDone completion detection + `TryStartNext` serial advance) with three required semantic triggers that must remain: `EpicAutoDoneHandler` (completed) and the renamed cancelled handler share the terminal-event dispatcher, while `EpicGrain.ResumeAsync` (`:524`) calls the internal core directly. The issue does not require these to be the only future triggers; D5 may add a bounded readiness trigger if that is the chosen replacement for sweep behavior. Only the name changes:

| Current | New |
|---|---|
| `IEpicGrain.ReconcileAfterTerminalAsync` | `RecomputeProgressAsync` |
| `EpicGrain.ReconcileAfterTerminalInternalAsync` | `RecomputeProgressInternalAsync` |
| `EpicReconcileDispatcher` (`EpicAutoDoneHandler.cs:90`) | `EpicProgressRecomputeDispatcher` |
| `EpicCancelledReconcileHandler` (`EpicAutoDoneHandler.cs:54`) | `EpicCancelledHandler` (style-aligned with `EpicAutoDoneHandler`; "Reconcile" dropped) |
| All XML doc / `<see cref>` "reconcile" wording | "recompute progress" |

The dispatcher's `DispatchAsync` body is structurally unchanged (it resolves the epic and calls `RecomputeProgressAsync`). `RecomputeProgressInternalAsync`'s successful progression behavior is unchanged: skip terminal/paused; MarkDone when no open linked issues remain; `TryStartNext` for running; no-op for idle. D5 deliberately changes only the failure contract for the public terminal-event entry. Idempotency is preserved (safe under at-least-once redelivery).

**Alternatives:**
- *`AdvanceAsync` / `OnTerminalMemberAsync`.* Less descriptive of the "recompute from member state, then act" behavior. The spec's chosen name (`RecomputeProgress`) is retained.
- *Drop the grain method and inline its logic across the event and resume paths.* Loses the shared idempotent core and the `ResumeAsync` reuse. Rejected — the method is domain logic, not a patch.

### D7 — Concurrency characteristic tests as **spec** tests (grain-speed), via `InProcessTestCluster` + in-memory SQLite + `FakeTimeProvider`

The whole point is to verify the real Orleans turn model, so the tests must run the real runtime. Per `design/testing.md`, grain-fixture tests using `InProcessTestCluster` are **spec** track (high integration through the product entry point), not unit track — and the UnitTests csproj backstop bans `Orleans.TestingHost` anyway. Mirror the existing `WorkflowGrainFixture` pattern: `InProcessTestClusterBuilder` + `MigratedSqliteTemplate.CopyTo` (no `Migrate()`) + `FakeTimeProvider` + `ControllableReminderTable`.

One test file for this issue (RunnerGrain concurrency specs are deferred to the follow-up prerequisite):
- `WorkflowGrainConcurrencySpecs` — issue concurrent control operations only from valid, deliberately prepared lifecycle phases; assert each outcome is one allowed complete serialized outcome and that the in-memory run state and persisted snapshot agree. Do not assume scheduler order across incompatible start/pause/resume/retry/rerun transitions.

Tests assert on **final settled state**, never on interleaving timing. No wall-clock waits; time advances via `FakeTimeProvider.Advance`. No real clustering/DB.

**Alternatives:**
- *Unit tests with a mocked grain.* Would not exercise the turn model under test — defeating the purpose. Rejected.
- *RunnerGrain concurrency specs in this issue.* RunnerGrain reentrancy removal is deferred; testing a model that has not changed adds no value. Deferred to the follow-up prerequisite.

### D8 — Implementation ordering within one atomic PR

1. Remove `[Reentrant]` from `WorkflowGrain` and add `WorkflowGrainConcurrencySpecs` (D1, D7) — compile + green.
2. Handler try-catch removal (D3, D4) + `RunnerWorkflowTerminalStatusHandler` prose verification — compile + green.
3. Epic recompute rename across contract/impl/dispatcher/handlers/docs/tests (D6) — `TreatWarningsAsErrors` catches missed `<see cref>`/call-site references at compile time.
4. Add link-time `RecomputeProgressInternalAsync` call after non-wake links to non-terminal epics; then delete the sweep + registration (D5) — compile + green, and confirm `EpicReconciliation*` has no remaining references.

Each step compiles and passes `npm test` before the next begins.

## Risks / Trade-offs

- **[Runner deadlock after removing `[Reentrant]`]** -> RunnerGrain reentrancy removal is deferred to a follow-up prerequisite. The multi-call poll lease and reciprocal Runner↔AgentJob waits are known blocking paths, not hypothetical self-call risks. The prerequisite resolves both protocols and verifies them through the real Orleans runtime.
- **[Epic progress regression after sweep deletion]** -> The sweep observes link-time and readiness transitions beyond missed completed/cancelled delivery. D5 resolves this by adding a link-time `RecomputeProgressInternalAsync` call after non-wake links to non-terminal epics. Draft/prerequisite readiness is covered by durable delivery of completed events.
- **[Next-issue start failure is silently acknowledged]** -> Use D5's explicit failure mode: terminal-event recompute propagates to the dispatcher; user command paths preserve running-but-idle behavior.
- **[Dead-lettered terminal event stalls epic until operator re-delivery]** -> This is the documented durable-delivery contract: the DLQ is queryable and manually retryable; the event is not silently absorbed. Acceptable per spec; operators need visibility on the DLQ.
- **[Renaming breaks references]** -> `IEpicGrain` is an Orleans grain contract with no external consumers. All call sites and tests are in-repo. `TreatWarningsAsErrors` + `<see cref>` resolution make missed references a compile error, not a runtime surprise. Test method names referencing `ReconcileAfterTerminalAsync_*` in `EpicProgressionSpecs.cs` / `EpicAutoDoneSpecs.cs` / `EpicAutoDoneHandlerSpecs.cs` are renamed in the same change.
- **[Concurrency specs turn flaky]** -> Assert only on settled final state; use `FakeTimeProvider` (no wall-clock waits); no order dependencies. If a flake appears, fix or delete it — `it.skip` is forbidden (`design/testing.md`).

## Migration Plan

- **Scope:** server-internal only. No web / CLI / runner / HTTP-contract change, no persistence schema change, no data migration.
- **Deploy:** single atomic PR, merged via the existing workflow. No special rollout or config migration is required; `EpicReconciliationOptions` is not bound to a deployed configuration section.
- **Compatibility:** grain persistent-state shapes are unchanged — `WorkflowGrain`/`RunnerGrain` state persists fine across the change (only an attribute and names are removed in this issue; the `RunnerGrain._worksStateWriteGate` semaphore is deferred). No on-disk format drift.
- **Rollback:** revert the PR. Since no schema/data migration ran, rollback needs no data step. Existing persisted grain state remains valid.
- **Verification gates:** `npm run build` (full solution, `TreatWarningsAsErrors` catches missed cref/references) and `npm test` (server specs, including the new concurrency specs). Each D8 step is green before the next.

## Open Questions

- **Runner coordination (deferred):** A follow-up prerequisite must remove the multi-call poll lease and reciprocal Runner↔AgentJob awaits without weakening authority-state serialization or modifying the `DispatchService` scheduling reconcile. This is assigned to the prerequisite, not this issue.
- **Cancelled handler final name:** `EpicCancelledHandler` is the most concise and style-aligned with `EpicAutoDoneHandler` (spec says "drop reconcile, style-aligned"). Confirm `EpicCancelledHandler` vs `EpicCancelledProgressHandler` at implementation time. Default: `EpicCancelledHandler`.
- **`using Orleans.Concurrency` removal:** Drop the `using` in `WorkflowGrain.cs` only if no other symbol from that namespace remains after the attribute is removed. `RunnerGrain.cs` retains `[Reentrant]` in this issue, so its `using` stays. Verify at edit time (the build will flag a leftover unused `using` under the existing analyzer set if configured).
