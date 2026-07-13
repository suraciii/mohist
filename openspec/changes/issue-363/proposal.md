## Why

Issue #362 landed the self-driving durable event dispatcher — events are now delivered at-least-once out-of-stack, with per-handler retry, backoff, and dead-lettering. The `[Reentrant]` marks on `WorkflowGrain` and `RunnerGrain`, the `RunnerGrain._worksStateWriteGate` semaphore, the handler-internal try-catch swallowing, and the `EpicReconciliationService` 10-minute sweep were all patches that existed to compensate for the old synchronous in-stack delivery model. With delivery out-of-stack, every one of these patches lost its reason: reentrancy reintroduces concurrency the turn-based serialization model already forbids, swallowed exceptions hide failures the dispatcher now retries, and a sweep for missed terminal events is moot when delivery is durable. Separately, `ReconcileAfterTerminalAsync` is a misnomer — the method recomputes epic progress (MarkDone detection + TryStartNext advance) from member state, it does not "reconcile" a discrepancy — and the word collides with the workflow scheduling domain's separate `DispatchService` reconcile. Cleaning all of this up lets the code say what it means.

## What Changes

- Remove `[Reentrant]` from `WorkflowGrain` (`Workflow/Grains/WorkflowGrain.cs:22`) and `RunnerGrain` (`Runner/Grains/RunnerGrain.cs:33`), restoring turn-based serialization as the sole state-safety guarantee for authority grains.
- Remove `RunnerGrain._worksStateWriteGate` (`RunnerGrain.cs:43`) and the gate-wrapped `PersistAsync` pattern; turn serialization makes the write gate redundant.
- Remove handler-internal try-catch exception swallowing (`AgentSubscriptionDispatchHandler.HandleAsync`, `HermesIssueNotificationHandler.HandleAsync`) so errors propagate to the durable dispatcher's unified retry/dead-letter path. (The `RunnerWorkflowTerminalStatusHandler` detach was already removed in #362; any remaining prose referencing the old model is cleaned up.)
- Remove `EpicReconciliationService` (`Events/Hosting/EpicReconciliationService.cs`), `EpicReconciliationOptions`, and the `AddHostedService<EpicReconciliationService>()` registration in `MohistServiceRegistration.cs`. Durable delivery of `com.mohist.issue.completed/cancelled` makes the 10-minute scan-based safety net unnecessary.
- Rename the epic progress-recompute chain from "reconcile" to "recompute progress" across the full call path:
  - `IEpicGrain.ReconcileAfterTerminalAsync` → `RecomputeProgressAsync` (grain contract)
  - `EpicGrain.ReconcileAfterTerminalInternalAsync` → `RecomputeProgressInternalAsync` (shared core logic, called from grain entry + `ResumeAsync`)
  - `EpicReconcileDispatcher` → `EpicProgressRecomputeDispatcher`
  - `EpicCancelledReconcileHandler` → rename to drop "reconcile" (style-aligned with `EpicAutoDoneHandler`)
  - All XML doc comments and `<see cref>` references updated from "reconcile" to "recompute progress"
- Add concurrency characteristic tests covering state consistency after reentrancy removal (both `WorkflowGrain` and `RunnerGrain`).

## Capabilities

- `grain-turn-serialization`: Authority grains (`WorkflowGrain`, `RunnerGrain`) rely solely on Orleans turn-based serialization for state safety — no `[Reentrant]`, no manual write gates. Concurrency tests verify state consistency under the serialized turn model.
- `handler-error-propagation`: Event handlers (`ICloudEventHandler` implementations) let exceptions propagate to the durable dispatcher for unified retry and dead-lettering. Handlers no longer detach, swallow, or log-and-continue on delivery failures.
- `epic-progress-recompute`: Epic progress recomputation on member terminal events is named and documented as "recompute progress" (not "reconcile"). The poll-driven safety-net sweep is removed; durable event delivery is the sole trigger. The grain method itself is retained — it is the epic's self-driving domain action (MarkDone detection + serial next-issue advance), with three call sites: `EpicAutoDoneHandler` (completed events), the renamed cancelled handler, and `ResumeAsync`.

## Impact

- **Server / `Workflow` slice**: `WorkflowGrain.cs` — remove `[Reentrant]` attribute (line 22) and `using Orleans.Concurrency` if no longer needed.
- **Server / `Runner` slice**: `RunnerGrain.cs` — remove `[Reentrant]` attribute (line 33), `_worksStateWriteGate` field (line 43), and simplify `PersistAsync` to call `_worksState.WriteStateAsync()` directly.
- **Server / `Events/Subscriptions`**: `AgentSubscriptionDispatchHandler.cs` — remove the try-catch wrapper in `HandleAsync` (lines 79-92). `HermesIssueNotificationHandler.cs` — remove the try-catch wrapper in `HandleAsync` (lines 50-74). `RunnerWorkflowTerminalStatusHandler.cs` — prose cleanup if any stale references remain (detach already removed in #362). `EpicAutoDoneHandler.cs` — rename `EpicReconcileDispatcher` → `EpicProgressRecomputeDispatcher`, update XML doc comments and `<see cref>` references.
- **Server / `Events/Hosting`**: Delete `EpicReconciliationService.cs` (contains both the hosted service and `EpicReconciliationOptions`).
- **Server / `Infrastructure/Hosting`**: `MohistServiceRegistration.cs` — remove `services.AddHostedService<EpicReconciliationService>()` registration (line 98).
- **Server / `Epic` slice**: `IEpicGrain.cs` — rename `ReconcileAfterTerminalAsync` → `RecomputeProgressAsync`. `EpicGrain.cs` — rename `ReconcileAfterTerminalAsync` → `RecomputeProgressAsync`, `ReconcileAfterTerminalInternalAsync` → `RecomputeProgressInternalAsync`, update the call site in `ResumeAsync` (line 524) and all XML doc comments.
- **Server / tests**: Update all test references to the renamed methods/types across `EpicProgressionSpecs.cs`, `EpicAutoDoneSpecs.cs`, `EpicAutoDoneHandlerSpecs.cs`. Add new concurrency characteristic tests for `WorkflowGrain` and `RunnerGrain` state consistency without reentrancy.
- **No web / CLI / runner / API contract changes** — pure server-internal refactor. No HTTP endpoints change, no new external dependencies.
- **Non-goals**: Workflow scheduling reconcile (`DispatchService` / `design/workflow/scheduling.md` / epic #44) is a separate mechanism and is not touched. `AutoMarkDoneIfReadyAsync` is a separate readiness-check entry point, not in the rename scope. The workflow state machine is not rewritten.
