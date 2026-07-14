## Context

Issue #362 landed the self-driving durable event dispatcher (`design/eventbus.md`). Events are now delivered at-least-once **out-of-stack**: a cluster-singleton reminder queries undispatched rows, fans out to `ICloudEventHandler`s, retries per handler with backoff, and dead-letters on exhaustion. This removes the precondition that justified three categories of compensating patches:

- **`[Reentrant]` on authority grains** (`WorkflowGrain` `Workflow/Grains/WorkflowGrain.cs:22`, `RunnerGrain` `Runner/Grains/RunnerGrain.cs:33`). The mark existed so a synchronous in-stack handler could call back into the publishing grain without deadlocking. Delivery is now off-stack; no handler ever re-enters the publishing grain's turn.
- **`RunnerGrain._worksStateWriteGate`** (`RunnerGrain.cs:43`), a `SemaphoreSlim` serializing `WriteStateAsync`. It existed *because* reentrancy allowed turns to interleave; with turn serialization restored it is redundant.
- **Handler try-catch swallow** in `AgentSubscriptionDispatchHandler.HandleAsync` (`Events/Subscriptions/AgentSubscriptionDispatchHandler.cs:79-92`) and `HermesIssueNotificationHandler.HandleAsync` (`Events/Subscriptions/HermesIssueNotificationHandler.cs:50-74`). The swallow predates the dispatcher's unified retry/DLQ path; it now hides failures the dispatcher is built to recover from. (`RunnerWorkflowTerminalStatusHandler`'s detach was already removed in #362; remaining prose is stale.)
- **`EpicReconciliationService`** (`Events/Hosting/EpicReconciliationService.cs`) - a 10-minute `BackgroundService` sweep that re-invoked `ReconcileAfterTerminalAsync` on idle+running epics to cover missed `com.mohist.issue.completed/cancelled` events. Durable delivery makes the miss impossible to silently drop.

Separately, `ReconcileAfterTerminalAsync` is a misnomer: it recomputes epic progress from member state (MarkDone detection + `TryStartNext` serial advance), it does not "reconcile a discrepancy". The name collides with the workflow-scheduling domain's distinct `DispatchService` reconcile (`design/workflow/scheduling.md`, epic #44). The rename is part of this change.

Current state:
- `WorkflowGrain` declares `[Reentrant]` and exposes `IGrainFactory` via `IWorkflowGrainContext.Grains` but does **not** call back into itself via the factory (verified: no `GetGrain<IWorkflowGrain>` self-reference in the `Workflow` slice). Intra-grain helper calls (`WorkflowStageLockCoordinator`, `WorkflowStageInitializer`, `WorkflowWorkLifecycle`) are direct method calls, not scheduler-mediated.
- `RunnerGrain` declares `[Reentrant]`, owns `_worksStateWriteGate` and `_pollAdmissionGate`, and calls `GrainFactory.GetGrain<IWorkflowGrain>(...)` (cross-grain, not self). It also has a poll-admission lease: `TryBeginPollAsync` holds `_pollAdmissionGate` across later grain calls to `TouchPresenceAsync`, `GetInfoAsync`, `ReconcileAgentJobsAsync`, and finally `EndPollAsync`, while `UnregisterAsync` and `UpdateAsync` may wait for that lease. Without broad reentrancy, either lifecycle call can occupy the turn before the poll reaches its next call. Runner also awaits `AgentJobGrain` during closeout (`HandleTimeoutAsync` -> `CloseoutLostAsync` -> `AgentJobGrain.ReportResultAsync`/`FailAsync`) while `AgentJobGrain.TryAssignToRunnerAsync` can await `RunnerGrain.AssignAgentJobAsync`, creating a reciprocal progress dependency currently masked by broad Runner reentrancy.
- `EpicGrain.ReconcileAfterTerminalAsync` (`Epic/Grains/EpicGrain.cs:653`) + `ReconcileAfterTerminalInternalAsync` (`:683`) have three required semantic triggers: completed and cancelled handlers share one dispatcher call to the grain entry, while `EpicGrain.ResumeAsync` (`:524`) calls the internal method directly. The hosted sweep is an additional current caller of the public grain entry.
- `HermesIssueNotificationHandler` uses a two-phase model: `HandleAsync` does synchronous setup (options/type resolution + enqueue) then returns; `_dispatcher.Dispatch` (`BackgroundHermesIssueNotificationDispatcher`) runs `DeliverAsync` on a background `Task.Run`. The background delivery is the best-effort notification channel (`design/architecture.md` "Events: two channels": domain reaction = durable, UI push = best-effort).
- `EpicReconciliationService` scans every idle and running epic. In addition to retrying missed terminal recomputes, it can observe a startable issue linked to an already-running epic, readiness changes that do not emit completed/cancelled events, terminal state that predates membership, and idle epics whose members are already complete.

Stakeholders: Server slice owners (Workflow, Runner, Events, Epic). No web/CLI/runner/API-contract stakeholders - pure server-internal refactor.

## Goals / Non-Goals

**Goals:**
- Restore Orleans turn-based serialization for authority-state mutations in `WorkflowGrain` and `RunnerGrain`; remove broad `[Reentrant]`, `RunnerGrain._worksStateWriteGate`, and `RunnerGrain._pollAdmissionGate`.
- Prevent reciprocal Runner↔AgentJob deadlock by marking `GetRuntimeStateAsync`, `GetSlotsAsync`, and `AssignAgentJobAsync` as `[AlwaysInterleave]` and moving closeout outside `_lifecycleGate` in `HandleTimeoutAsync`.
- Collapse error handling for the covered durable processing stages to the dispatcher's single aggregation point; preserve intentionally best-effort UI-delivery error handling.
- Remove the poll-driven epic terminal-recompute sweep; make durable event delivery reliable for member terminal events while preserving or explicitly changing the other readiness behavior the sweep currently supplies (manual `ResumeAsync` re-evaluation remains).
- Rename the epic progress-recompute chain from "reconcile" to "recompute progress" across the contract, implementation, dispatcher, handlers, docs, and tests - while preserving the grain method and its three semantic triggers.
- Add concurrency characteristic tests proving the reentrancy removal does not introduce torn state for both `WorkflowGrain` and `RunnerGrain`.

**Non-Goals:**
- Rewrite the workflow state machine or change cross-aggregate event->command orchestration semantics.
- Rewrite workflow-scheduling reconcile (`DispatchService` / `design/workflow/scheduling.md` / epic #44). The DispatchService poll order remains unchanged; fresh workflow claims revalidate live Runner availability and capacity.
- Change `AutoMarkDoneIfReadyAsync` (a separate readiness entry, not in the rename scope).
- Make Hermes notification delivery durable (it is intentionally best-effort; only its setup phase propagates to the dispatcher).
- Any web / CLI / runner / HTTP contract change, new external dependency, or persistence schema change.

## Decisions

### D1 - Remove broad `[Reentrant]` from WorkflowGrain

Turn serialization is the intended guarantee for authority-state mutations (`design/architecture.md`: "Authority grains: no `[Reentrant]`"). With delivery off-stack (#362), the original synchronous-handler callback rationale is gone. `WorkflowGrain` has no scheduler-mediated self-call or later-call release dependency, so its broad attribute can be removed directly.

**Alternatives:**
- *Keep `[Reentrant]` + explicit locking.* Re-adds the complexity being removed and hides the real guarantee behind ad-hoc locks. Rejected.
- *Keep `[Reentrant]` as a no-op for future flexibility.* The whole point of the issue is that it is not a no-op - it re-enables interleaving. Remove it.

**Scope decision:** Audit `WorkflowGrain` for any `await` path that resolves a call back into the **same grain activation** (verified: none exists). Cover the restored turn model with real Orleans characteristic tests for `WorkflowGrain`.

### D2 - Remove broad `[Reentrant]` from RunnerGrain; remove `_worksStateWriteGate` and `_pollAdmissionGate`; add `[AlwaysInterleave]` to prevent reciprocal deadlock

`RunnerGrain` cannot simply drop `[Reentrant]` without addressing two deadlock risks that reentrancy was masking. Both are resolved without modifying `DispatchService` or its poll call sequence.

**Deadlock 1: Multi-call poll admission gate.** `TryBeginPollAsync` acquires `_pollAdmissionGate` (a `SemaphoreSlim`), and the poll sequence spans multiple separate grain calls (`TouchPresenceAsync`, `GetInfoAsync`, `ReconcileAgentJobsAsync`, `EndPollAsync`). Between calls, `UnregisterAsync` or `UpdateAsync` can enter the grain turn and block on `_pollAdmissionGate.WaitAsync()`, preventing the poll's next call from entering the turn. Classic hold-and-wait deadlock.

**Fix:** Remove `_pollAdmissionGate` entirely. Keep `_pollAdmitted` as a boolean flag: `TryBeginPollAsync` sets it, `EndPollAsync` clears it, `AssignAgentJobAsync` checks it and rejects with "runner-reconciling" (existing behavior). `UnregisterAsync` and `UpdateAsync` no longer wait on a poll-admission semaphore - they proceed and acquire `_lifecycleGate` as normal. With turn serialization, between poll calls these methods can run, but the poll is resilient to state changes between calls (each call re-reads current state). If `UnregisterAsync` runs between poll calls, it sets the runner offline; subsequent poll calls observe the offline state and return appropriately.

**Deadlock 2: Reciprocal Runner↔AgentJob.** `HandleTimeoutAsync` (presence timer) holds `_lifecycleGate` and calls `CloseoutLostAsync`, which calls `AgentJobGrain.ReportResultAsync`/`FailAsync`. Concurrently, `AgentJobGrain.TryAssignToRunnerAsync` calls `RunnerGrain.AssignAgentJobAsync`. `AgentJobGrain` is non-reentrant (confirmed: class doc says "Persisted and non-reentrant"). Without Runner reentrancy, `AssignAgentJobAsync` queues behind `HandleTimeoutAsync`'s turn, while `ReportResultAsync` queues behind `TryAssignToRunnerAsync`'s turn. Deadlock.

Analysis of the call graph (verified from source):
- `AgentJobGrain.IsWorkRunnableAsync` is a pure synchronous read (`Task.FromResult`) - no callback into RunnerGrain. Safe during `ReconcileAgentJobsAsync`.
- `AgentJobGrain.ReportResultAsync` calls `IAgentSessionGrain` (not RunnerGrain). No direct callback, but `TryAssignToRunnerAsync` on the same AgentJob activation can be in progress.
- `AgentJobGrain.TryAssignToRunnerAsync` calls `runner.GetRuntimeStateAsync()` (line 418), `runner.GetSlotsAsync()` (line 422, already `[AlwaysInterleave]`), then `runner.AssignAgentJobAsync(dispatch)` (line 442).

**Fix (three parts):**

1. **Mark `GetRuntimeStateAsync` and `AssignAgentJobAsync` as `[AlwaysInterleave]`** on `IRunnerGrain`. `GetSlotsAsync` is already `[AlwaysInterleave]`. This allows `TryAssignToRunnerAsync`'s calls to execute even when RunnerGrain's turn is held by closeout. `GetRuntimeStateAsync` is read-only (DB queries + in-memory reads) - stale reads are acceptable and `AssignAgentJobAsync` performs its own capacity check. `AssignAgentJobAsync` mutates state, so it MUST acquire `_lifecycleGate` before mutating - the gate serializes its state changes with other lifecycle methods.

2. **Move `CloseoutLostAsync()` outside `_lifecycleGate` in `HandleTimeoutAsync`.** This matches the existing `UnregisterAsync` pattern (which already calls `CloseoutLostAsync` at line 181 after releasing both gates). `HandleTimeoutAsync` sets `_status = Offline` and unregisters inside `_lifecycleGate`, releases the gate, then calls `CloseoutLostAsync`. This ensures `_lifecycleGate` is free when `AssignAgentJobAsync` (interleaved) tries to acquire it.

3. **Keep `_lifecycleGate`.** It is no longer redundant: `AssignAgentJobAsync` is `[AlwaysInterleave]`, so it can interleave with turn-serialized methods. `_lifecycleGate` serializes `AssignAgentJobAsync`'s state mutations with `RegisterAsync`, `UnregisterAsync`, `TouchPresenceAsync`, `ReconcileAgentJobsAsync`, etc. Without the gate, interleaved `AssignAgentJobAsync` could race with `ReconcileAgentJobsAsync` on the works list.

**`_worksStateWriteGate` removal:** With turn serialization restored for non-interleaved methods, `PersistAsync` calls from turn-serialized methods cannot overlap. `AssignAgentJobAsync` (interleaved) calls `PersistAsync` while holding `_lifecycleGate`; `CloseoutLostAsync` (running outside `_lifecycleGate` during closeout) calls `PersistAsync` but only after the runner is offline - `AssignAgentJobAsync` rejects offline runners, so no concurrent `PersistAsync` from `AssignAgentJobAsync`. Other interleavable methods (`GetRuntimeStateAsync`, `GetSlotsAsync`) are read-only and don't call `PersistAsync`. Therefore `_worksStateWriteGate` is redundant and removed. `PersistAsync` calls `WriteStateAsync` directly. All 11 `PersistAsync` call sites remain semantically unchanged.

**Deadlock verification for all scenarios:**

- *Poll + Unregister:* `_pollAdmitted` flag rejects `AssignAgentJobAsync`; `UnregisterAsync` doesn't block on `_pollAdmissionGate`. No deadlock.
- *HandleTimeout + TryAssignToRunner:* `AssignAgentJobAsync` is `[AlwaysInterleave]`, executes during closeout, acquires `_lifecycleGate` (free - closeout runs outside it), rejects (runner offline), `TryAssignToRunnerAsync` completes, AgentJobGrain freed, `ReportResultAsync` executes. No deadlock.
- *ReconcileAgentJobs + TryAssignToRunner (during poll):* `AssignAgentJobAsync` rejects via `_pollAdmitted` flag (no gate needed). `TryAssignToRunnerAsync` completes. `IsWorkRunnableAsync` executes. No deadlock.
- *ReportAgentJobResult + TryAssignToRunner:* `ReportAgentJobResultAsync` doesn't hold `_lifecycleGate`; `AssignAgentJobAsync` interleaves, acquires gate, completes. No deadlock.

**Alternatives:**
- *Defer RunnerGrain reentrancy removal to a follow-up prerequisite.* Contradicts the issue acceptance criteria which explicitly require removing `[Reentrant]` from both grains and `_worksStateWriteGate`. Rejected.
- *Keep `[Reentrant]` + explicit locking.* Re-adds the complexity being removed. Rejected.
- *Remove `_lifecycleGate` entirely.* Unsafe: `AssignAgentJobAsync` is `[AlwaysInterleave]` and needs explicit state-mutation protection. Rejected.
- *Use `Task.Run` for closeout.* Anti-pattern in Orleans (bypasses grain scheduler). Rejected.

### D3 - Remove the try-catch swallow in `AgentSubscriptionDispatchHandler.HandleAsync`; keep envelope-level no-op returns

Drop the `catch (Exception ex) { _log.LogWarning(...); }` wrapper (`AgentSubscriptionDispatchHandler.cs:79-92`). Let the `OperationCanceledException`-when-cancelled rethrow remain (cooperative cancellation, per spec). The envelope-level skips (no `projectid`, no active matched subscription, empty rendered prompt) stay as `return` without throwing - those are valid no-ops, not failures. The inner `DispatchAsync` body is unchanged.

**Alternatives:**
- *Catch + log + a structured "delivery failed" signal.* Re-invents what the dispatcher already does (retry + DLQ). Rejected.

### D4 - Remove the setup try-catch in `HermesIssueNotificationHandler.HandleAsync`; keep the background `DeliverAsync` try-catch and the two-phase model

The handler is two-phase: synchronous **setup** (options/type resolution + `_dispatcher.Dispatch` enqueue) returns `Task.CompletedTask`; **delivery** runs in the background via `BackgroundHermesIssueNotificationDispatcher.Task.Run`. Per `design/architecture.md`'s two-channel split, notification delivery is best-effort UI push, not a durable domain reaction. Therefore:

- Remove the outer try-catch in `HandleAsync` (`HermesIssueNotificationHandler.cs:50-74`). **Setup** failures (options resolution, type resolution, dispatch enqueue) now propagate to the durable dispatcher, which retries/dead-letters. The disabled-notification and unconfigured-webhook early returns stay as no-ops.
- Keep `DeliverAsync`'s own try-catch (`:79-101`) and `BackgroundHermesIssueNotificationDispatcher`'s `Task.Run` catch. They are the best-effort channel's own error handling; delivery is intentionally off the durable dispatch stack.

The spec's "handler SHALL NOT detach its side effect to hide a failure" applies to the handler's `HandleAsync` return contract - which is preserved: setup failures propagate, only the best-effort *delivery* is detached by design. This is not a patch; it is the two-channel architecture.

**Alternatives:**
- *Make notification delivery durable.* Wrong channel; out of scope; would couple a best-effort UI push to the durable domain-reaction contract. Rejected.
- *Remove the `DeliverAsync` try-catch too.* Then background failures surface only in `BackgroundHermesIssueNotificationDispatcher`'s generic catch, losing the cancellation-vs-failure log distinction. Rejected - the background catch is the channel's own concern, not the dispatcher's.

### D5 - Delete `EpicReconciliationService` + `EpicReconciliationOptions` + the `AddHostedService<EpicReconciliationService>()` registration

Durable delivery of `com.mohist.issue.completed/cancelled` becomes the reliable automatic trigger for recompute in response to member terminal events. A dead-lettered event stays in the DLQ for operator re-delivery - not silently dropped - so the sweep's missed-event rationale disappears. Delete `Events/Hosting/EpicReconciliationService.cs` (contains both the `BackgroundService` and `EpicReconciliationOptions`) and remove the `services.AddHostedService<EpicReconciliationService>()` line (`MohistServiceRegistration.cs:98`). Keep `using Mohist.Server.Events.Hosting;` because `DispatcherActivationService` remains registered from that namespace.

**Resolved error contract:** `TryStartNextAsync` currently catches `IIssueGrain.StartWorkAsync` failures and returns success. Add an explicit private start-failure mode with `PreserveRunning` and `Propagate`. `RecomputeProgressAsync` calls `RecomputeProgressInternalAsync` with `Propagate`, so terminal-event failures escape to the durable dispatcher. `StartAsync`, link operations, and `ResumeAsync` use `PreserveRunning`, retaining their current running-but-idle behavior. Implement the catch as a filtered catch for `PreserveRunning`; `Propagate` failures are not logged-and-swallowed locally. Tests cover both terminal-event propagation and the unchanged command paths.

**Link-time recompute trigger (resolved):** the service is not only a missed-event retry. Its scan can make progress after a startable issue is linked to a running epic, and when an issue was already terminal before it was linked. It also marks idle epics done when all members are complete at link time. These behaviors are preserved by adding a `RecomputeProgressInternalAsync` call after non-wake links to non-terminal epics in `LinkIssueAsync` and `LinkIssuesAsync`. Draft/prerequisite readiness is covered by durable delivery of the prerequisite's `completed` event. The sweep is deleted after the link-time trigger is in place. No cross-aggregate event->command semantics are changed.

**Alternatives:**
- *Keep the sweep at a slower cadence as defense-in-depth.* Re-introduces the patch the issue removes, conflates a poll-driven scan with event-driven recompute, and adds load for no durable benefit. Rejected.
- *Keep `EpicReconciliationOptions` inert.* Dead config surface. Rejected.

### D6 - Rename the epic progress-recompute chain; keep the grain method and all three required semantic triggers

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
- *Drop the grain method and inline its logic across the event and resume paths.* Loses the shared idempotent core and the `ResumeAsync` reuse. Rejected - the method is domain logic, not a patch.

### D7 - Concurrency characteristic tests as **spec** tests (grain-speed), via `InProcessTestCluster` + in-memory SQLite + `FakeTimeProvider`

The whole point is to verify the real Orleans turn model, so the tests must run the real runtime. Per `design/testing.md`, grain-fixture tests using `InProcessTestCluster` are **spec** track (high integration through the product entry point), not unit track - and the UnitTests csproj backstop bans `Orleans.TestingHost` anyway. Mirror the existing `WorkflowGrainFixture` pattern: `InProcessTestClusterBuilder` + `MigratedSqliteTemplate.CopyTo` (no `Migrate()`) + `FakeTimeProvider` + `ControllableReminderTable`.

Two test files for this issue:
- `WorkflowGrainConcurrencySpecs` - issue concurrent control operations only from valid, deliberately prepared lifecycle phases; assert each outcome is one allowed complete serialized outcome and that the in-memory run state and persisted snapshot agree. Do not assume scheduler order across incompatible start/pause/resume/retry/rerun transitions.
- `RunnerGrainConcurrencySpecs` - issue concurrent lifecycle operations (register, unregister, update, timeout) and poll operations (try-begin, touch-presence, reconcile, end) from prepared phases; assert that in-memory status, presence, and works ledger remain consistent and the persisted snapshot agrees. Include a scenario where `HandleTimeoutAsync` fires while `AgentJobGrain.TryAssignToRunnerAsync` is in progress, asserting no deadlock and correct final state (runner offline, assignment rejected, closeout completed).

Tests assert on **final settled state**, never on interleaving timing. No wall-clock waits; time advances via `FakeTimeProvider.Advance`. No real clustering/DB.

**Alternatives:**
- *Unit tests with a mocked grain.* Would not exercise the turn model under test - defeating the purpose. Rejected.
- *RunnerGrain concurrency specs only after a follow-up.* RunnerGrain reentrancy removal is in this issue; testing it here is required.

### D8 - Implementation ordering within one atomic PR

1. Remove `[Reentrant]` from `WorkflowGrain` and add `WorkflowGrainConcurrencySpecs` (D1, D7) - compile + green.
2. Remove `[Reentrant]` from `RunnerGrain`, remove `_worksStateWriteGate` and `_pollAdmissionGate`, add `[AlwaysInterleave]` to `GetRuntimeStateAsync` and `AssignAgentJobAsync`, move `CloseoutLostAsync` outside `_lifecycleGate` in `HandleTimeoutAsync`, add `RunnerGrainConcurrencySpecs` (D2, D7) - compile + green.
3. Handler try-catch removal (D3, D4) + `RunnerWorkflowTerminalStatusHandler` prose verification - compile + green.
4. Epic recompute rename across contract/impl/dispatcher/handlers/docs/tests (D6) - `TreatWarningsAsErrors` catches missed `<see cref>`/call-site references at compile time.
5. Add link-time `RecomputeProgressInternalAsync` call after non-wake links to non-terminal epics; then delete the sweep + registration (D5) - compile + green, and confirm `EpicReconciliation*` has no remaining references.

Each step compiles and passes `npm test` before the next begins.

## Risks / Trade-offs

- **[Runner deadlock after removing `[Reentrant]`]** -> Resolved by D2: `_pollAdmissionGate` removal eliminates the multi-call poll-lease deadlock; `[AlwaysInterleave]` on `GetRuntimeStateAsync`/`AssignAgentJobAsync` + `CloseoutLostAsync` outside `_lifecycleGate` eliminates the reciprocal Runner↔AgentJob deadlock. All four deadlock scenarios verified (poll+unregister, timeout+assignment, reconcile+assignment, report+assignment).
- **[Interleaved `AssignAgentJobAsync` races with lifecycle methods]** -> `AssignAgentJobAsync` acquires `_lifecycleGate` before mutating state, serializing with other lifecycle methods. Read-only interleavable methods (`GetRuntimeStateAsync`, `GetSlotsAsync`) tolerate stale reads; `AssignAgentJobAsync` performs its own capacity and status checks.
- **[Epic progress regression after sweep deletion]** -> The sweep observes link-time and readiness transitions beyond missed completed/cancelled delivery. D5 resolves this by adding a link-time `RecomputeProgressInternalAsync` call after non-wake links to non-terminal epics. Draft/prerequisite readiness is covered by durable delivery of completed events.
- **[Next-issue start failure is silently acknowledged]** -> Use D5's explicit failure mode: terminal-event recompute propagates to the dispatcher; user command paths preserve running-but-idle behavior.
- **[Dead-lettered terminal event stalls epic until operator re-delivery]** -> This is the documented durable-delivery contract: the DLQ is queryable and manually retryable; the event is not silently absorbed. Acceptable per spec; operators need visibility on the DLQ.
- **[Renaming breaks references]** -> `IEpicGrain` is an Orleans grain contract with no external consumers. All call sites and tests are in-repo. `TreatWarningsAsErrors` + `<see cref>` resolution make missed references a compile error, not a runtime surprise. Test method names referencing `ReconcileAfterTerminalAsync_*` in `EpicProgressionSpecs.cs` / `EpicAutoDoneSpecs.cs` / `EpicAutoDoneHandlerSpecs.cs` are renamed in the same change.
- **[Concurrency specs turn flaky]** -> Assert only on settled final state; use `FakeTimeProvider` (no wall-clock waits); no order dependencies. If a flake appears, fix or delete it - `it.skip` is forbidden (`design/testing.md`).

## Migration Plan

- **Scope:** server-internal only. No web / CLI / runner / HTTP-contract change, no persistence schema change, no data migration.
- **Deploy:** single atomic PR, merged via the existing workflow. No special rollout or config migration is required; `EpicReconciliationOptions` is not bound to a deployed configuration section.
- **Compatibility:** grain persistent-state shapes are unchanged - `WorkflowGrain`/`RunnerGrain` state persists fine across the change (only attributes, semaphores, and names are removed). No on-disk format drift.
- **Rollback:** revert the PR. Since no schema/data migration ran, rollback needs no data step. Existing persisted grain state remains valid.
- **Verification gates:** `npm run build` (full solution, `TreatWarningsAsErrors` catches missed cref/references) and `npm test` (server specs, including the new concurrency specs). Each D8 step is green before the next.

## Open Questions

- **Cancelled handler final name:** `EpicCancelledHandler` is the most concise and style-aligned with `EpicAutoDoneHandler` (spec says "drop reconcile, style-aligned"). Confirm `EpicCancelledHandler` vs `EpicCancelledProgressHandler` at implementation time. Default: `EpicCancelledHandler`.
- **`using Orleans.Concurrency` removal:** Drop the `using` in `WorkflowGrain.cs` only if no other symbol from that namespace remains after the attribute is removed. `RunnerGrain.cs` now uses `[AlwaysInterleave]` from `Orleans.Concurrency` (on `GetRuntimeStateAsync` and `AssignAgentJobAsync`), so its `using` stays. Verify at edit time (the build will flag a leftover unused `using` under the existing analyzer set if configured).
