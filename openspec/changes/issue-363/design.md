## Context

Issue #362 landed the self-driving durable event dispatcher (`design/eventbus.md`). Events are now delivered at-least-once **out-of-stack**: a cluster-singleton reminder queries undispatched rows, fans out to `ICloudEventHandler`s, retries per handler with backoff, and dead-letters on exhaustion. This removes the precondition that justified three categories of compensating patches:

- **`[Reentrant]` on authority grains** (`WorkflowGrain` `Workflow/Grains/WorkflowGrain.cs:22`, `RunnerGrain` `Runner/Grains/RunnerGrain.cs:33`). The mark existed so a synchronous in-stack handler could call back into the publishing grain without deadlocking. Delivery is now off-stack; no handler ever re-enters the publishing grain's turn.
- **`RunnerGrain._worksStateWriteGate`** (`RunnerGrain.cs:43`), a `SemaphoreSlim` serializing `WriteStateAsync`. It existed *because* reentrancy allowed turns to interleave; with turn serialization restored it is redundant.
- **Handler try-catch swallow** in `AgentSubscriptionDispatchHandler.HandleAsync` (`Events/Subscriptions/AgentSubscriptionDispatchHandler.cs:79-92`) and `HermesIssueNotificationHandler.HandleAsync` (`Events/Subscriptions/HermesIssueNotificationHandler.cs:50-74`). The swallow predates the dispatcher's unified retry/DLQ path; it now hides failures the dispatcher is built to recover from. (`RunnerWorkflowTerminalStatusHandler`'s detach was already removed in #362; remaining prose is stale.)
- **`EpicReconciliationService`** (`Events/Hosting/EpicReconciliationService.cs`) — a 10-minute `BackgroundService` sweep that re-invoked `ReconcileAfterTerminalAsync` on idle+running epics to cover missed `com.mohist.issue.completed/cancelled` events. Durable delivery makes the miss impossible to silently drop.

Separately, `ReconcileAfterTerminalAsync` is a misnomer: it recomputes epic progress from member state (MarkDone detection + `TryStartNext` serial advance), it does not "reconcile a discrepancy". The name collides with the workflow-scheduling domain's distinct `DispatchService` reconcile (`design/workflow/scheduling.md`, epic #44). The rename is part of this change.

Current state:
- `WorkflowGrain` declares `[Reentrant]` and exposes `IGrainFactory` via `IWorkflowGrainContext.Grains` but does **not** call back into itself via the factory (verified: no `GetGrain<IWorkflowGrain>` self-reference in the `Workflow` slice). Intra-grain helper calls (`WorkflowStageLockCoordinator`, `WorkflowStageInitializer`, `WorkflowWorkLifecycle`) are direct method calls, not scheduler-mediated.
- `RunnerGrain` declares `[Reentrant]`, owns `_worksStateWriteGate`, and calls `GrainFactory.GetGrain<IWorkflowGrain>(...)` (cross-grain, not self).
- `EpicGrain.ReconcileAfterTerminalAsync` (`Epic/Grains/EpicGrain.cs:653`) + `ReconcileAfterTerminalInternalAsync` (`:683`) are called from three sites: `EpicAutoDoneHandler` (completed events), `EpicCancelledReconcileHandler` (cancelled events), and `EpicGrain.ResumeAsync` (`:524`).
- `HermesIssueNotificationHandler` uses a two-phase model: `HandleAsync` does synchronous setup (options/type resolution + enqueue) then returns; `_dispatcher.Dispatch` (`BackgroundHermesIssueNotificationDispatcher`) runs `DeliverAsync` on a background `Task.Run`. The background delivery is the best-effort notification channel (`design/architecture.md` "Events: two channels": domain reaction = durable, UI push = best-effort).

Stakeholders: Server slice owners (Workflow, Runner, Events, Epic). No web/CLI/runner/API-contract stakeholders — pure server-internal refactor.

## Goals / Non-Goals

**Goals:**
- Restore Orleans turn-based serialization as the sole state-safety guarantee for `WorkflowGrain` and `RunnerGrain`; remove `[Reentrant]` and the manual write gate.
- Collapse handler error handling to the durable dispatcher's single aggregation point; remove handler-internal catch-log-return.
- Remove the poll-driven epic terminal-recompute sweep; make durable event delivery the sole trigger.
- Rename the epic progress-recompute chain from "reconcile" to "recompute progress" across the contract, implementation, dispatcher, handlers, docs, and tests — while preserving the grain method and its three call sites.
- Add concurrency characteristic tests proving the reentrancy removal does not introduce torn state.

**Non-Goals:**
- Rewrite the workflow state machine or change cross-aggregate event→command orchestration semantics.
- Touch workflow-scheduling reconcile (`DispatchService` / `design/workflow/scheduling.md` / epic #44).
- Change `AutoMarkDoneIfReadyAsync` (a separate readiness entry, not in the rename scope).
- Make Hermes notification delivery durable (it is intentionally best-effort; only its setup phase propagates to the dispatcher).
- Any web / CLI / runner / HTTP contract change, new external dependency, or persistence schema change.

## Decisions

### D1 — Remove `[Reentrant]` from both authority grains; drop the `using Orleans.Concurrency` if it becomes unused

Turn serialization is the intended guarantee for authority grains (`design/architecture.md`: "Authority grains: no `[Reentrant]`"). With delivery off-stack (#362), no handler re-enters the publishing grain, so the original deadlock precondition is gone. Reentrancy now only *weakens* the model by allowing interleaving the turn model already forbids.

**Alternatives:**
- *Keep `[Reentrant]` + explicit locking.* Re-adds the complexity being removed and hides the real guarantee behind ad-hoc locks. Rejected.
- *Keep `[Reentrant]` as a no-op for future flexibility.* The whole point of the issue is that it is not a no-op — it re-enables interleaving. Remove it.

**Precondition / verification:** Audit both grains for any `await` path that resolves a call back into the **same grain activation** via `IGrainFactory`/`GrainFactory.GetGrain<ThisGrain>`. Such a path would self-deadlock without reentrancy. Verified absent today: `WorkflowGrain` exposes `GrainFactory` through `IWorkflowGrainContext.Grains` but the slice never self-calls through it; `RunnerGrain` only calls `IWorkflowGrain` (cross-grain). The audit must be re-run at implementation time and is enforced by the new concurrency tests.

### D2 — Remove `RunnerGrain._worksStateWriteGate`; simplify `PersistAsync` to call `_worksState.WriteStateAsync()` directly

The gate serialized `WriteStateAsync` only because reentrancy let turns interleave. D1 restores one-turn-at-a-time execution, so two `PersistAsync` calls on the same activation cannot overlap. The persistence contract is unchanged: writes remain durable and a failed `WriteStateAsync` still propagates the exception to the caller. All 11 `PersistAsync` call sites (`RunnerGrain.cs:116,145,167,194,273,341,355,382,484,645,706`) are unchanged.

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

Durable delivery of `com.mohist.issue.completed/cancelled` is the sole trigger for terminal recompute. A dead-lettered event stays in the DLQ for operator re-delivery — not silently dropped — so the sweep's "missed event" rationale disappears. Delete `Events/Hosting/EpicReconciliationService.cs` (contains both the `BackgroundService` and `EpicReconciliationOptions`), remove the `services.AddHostedService<EpicReconciliationService>()` line (`MohistServiceRegistration.cs:98`), and drop the `using Mohist.Server.Events.Hosting;` import only if it becomes unused.

**Alternatives:**
- *Keep the sweep at a slower cadence as defense-in-depth.* Re-introduces the patch the issue removes, conflates a poll-driven scan with event-driven recompute, and adds load for no durable benefit. Rejected.
- *Keep `EpicReconciliationOptions` inert.* Dead config surface. Rejected.

### D6 — Rename the epic progress-recompute chain; keep the grain method and its three call sites

The method carries real domain logic (MarkDone completion detection + `TryStartNext` serial advance) with three call sites that must remain: `EpicAutoDoneHandler` (completed), the renamed cancelled handler, and `EpicGrain.ResumeAsync` (`:524`). Only the name changes:

| Current | New |
|---|---|
| `IEpicGrain.ReconcileAfterTerminalAsync` | `RecomputeProgressAsync` |
| `EpicGrain.ReconcileAfterTerminalInternalAsync` | `RecomputeProgressInternalAsync` |
| `EpicReconcileDispatcher` (`EpicAutoDoneHandler.cs:90`) | `EpicProgressRecomputeDispatcher` |
| `EpicCancelledReconcileHandler` (`EpicAutoDoneHandler.cs:54`) | `EpicCancelledHandler` (style-aligned with `EpicAutoDoneHandler`; "Reconcile" dropped) |
| All XML doc / `<see cref>` "reconcile" wording | "recompute progress" |

The dispatcher's `DispatchAsync` body is unchanged (it resolves the epic and calls `RecomputeProgressAsync`). `RecomputeProgressInternalAsync`'s behavior is unchanged: skip terminal/paused; MarkDone when no open linked issues remain; `TryStartNext` for running; no-op for idle. Idempotency is preserved (safe under at-least-once redelivery).

**Alternatives:**
- *`AdvanceAsync` / `OnTerminalMemberAsync`.* Less descriptive of the "recompute from member state, then act" behavior. The spec's chosen name (`RecomputeProgress`) is retained.
- *Drop the grain method and inline its logic at the three call sites.* Loses the shared idempotent core and the `ResumeAsync` reuse. Rejected — the method is domain logic, not a patch.

### D7 — Concurrency characteristic tests as **spec** tests (grain-speed), via `InProcessTestCluster` + in-memory SQLite + `FakeTimeProvider`

The whole point is to verify the real Orleans turn model, so the tests must run the real runtime. Per `design/testing.md`, grain-fixture tests using `InProcessTestCluster` are **spec** track (high integration through the product entry point), not unit track — and the UnitTests csproj backstop bans `Orleans.TestingHost` anyway. Mirror the existing `WorkflowGrainFixture` pattern: `InProcessTestClusterBuilder` + `MigratedSqliteTemplate.CopyTo` (no `Migrate()`) + `FakeTimeProvider` + `ControllableReminderTable`.

Two test files, one per grain:
- `WorkflowGrainConcurrencySpecs` — issue multiple control operations (start/pause/resume/retry/rerun) against one activation without reentrancy; assert in-memory run state and persisted snapshot remain internally consistent (no partially-applied turn).
- `RunnerGrainConcurrencySpecs` — issue multiple work-mutating operations (assign agent-job work, report result, closeout on presence loss) against one activation without reentrancy and without the write gate; assert the works ledger and persisted state remain internally consistent.

Tests assert on **final settled state**, never on interleaving timing. No wall-clock waits; time advances via `FakeTimeProvider.Advance`. No real clustering/DB.

**Alternatives:**
- *Unit tests with a mocked grain.* Would not exercise the turn model under test — defeating the purpose. Rejected.
- *A single shared concurrency spec.* Two different grains with different state shapes; one-file-one-subject (`design/testing.md`) favors a split.

### D8 — Implementation ordering within one atomic PR

1. Reentrancy + write-gate removal + concurrency specs (D1, D2, D7) — compile + green.
2. Handler try-catch removal (D3, D4) + `RunnerWorkflowTerminalStatusHandler` prose verification — compile + green.
3. Epic recompute rename across contract/impl/dispatcher/handlers/docs/tests (D6) — `TreatWarningsAsErrors` catches missed `<see cref>`/call-site references at compile time.
4. Sweep deletion + registration removal (D5) — compile + green, and confirm `EpicReconciliation*` has no remaining references.

Each step compiles and passes `npm test` before the next begins.

## Risks / Trade-offs

- **[Self-reentrancy deadlock after removing `[Reentrant]`]** -> Audit both grains for any `await` resolving a call back into the same activation via `GrainFactory`. Verified absent today; the D7 concurrency specs are the running safety net. Re-run the audit at implementation time.
- **[Idle epic stuck when all members were already complete at link time]** -> The sweep previously auto-marked-done idle epics in this state via its periodic readiness scan. With the sweep gone, an epic linked to already-complete issues (no terminal event fires *after* the link) stays idle until a terminal event or an explicit `AutoMarkDoneIfReadyAsync`. Durable terminal events cover the common path (events fire on completion); the edge is a behavior change. Mitigation: `AutoMarkDoneIfReadyAsync` remains an explicit entry. A link-time readiness check is a follow-up (out of scope; see Open Questions).
- **[Dead-lettered terminal event stalls epic until operator re-delivery]** -> This is the documented durable-delivery contract: the DLQ is queryable and manually retryable; the event is not silently absorbed. Acceptable per spec; operators need visibility on the DLQ.
- **[Renaming breaks references]** -> `IEpicGrain` is an Orleans grain contract with no external consumers. All call sites and tests are in-repo. `TreatWarningsAsErrors` + `<see cref>` resolution make missed references a compile error, not a runtime surprise. Test method names referencing `ReconcileAfterTerminalAsync_*` in `EpicProgressionSpecs.cs` / `EpicAutoDoneSpecs.cs` / `EpicAutoDoneHandlerSpecs.cs` are renamed in the same change.
- **[Concurrency specs turn flaky]** -> Assert only on settled final state; use `FakeTimeProvider` (no wall-clock waits); no order dependencies. If a flake appears, fix or delete it — `it.skip` is forbidden (`design/testing.md`).
- **[Stale `EpicReconciliation` config section in deployed configs]** -> After removing `EpicReconciliationOptions`, any leftover config section is silently ignored by the options binder. No error, but operators should be told to drop it. Documented in the PR description.

## Migration Plan

- **Scope:** server-internal only. No web / CLI / runner / HTTP-contract change, no persistence schema change, no data migration.
- **Deploy:** single atomic PR, merged via the existing workflow. No special rollout; no config migration required (a stale `EpicReconciliation` config section becomes inert and can be removed at operator leisure).
- **Compatibility:** grain persistent-state shapes are unchanged — `WorkflowGrain`/`RunnerGrain` state persists fine across the change (only an attribute, a semaphore, and names are removed). No on-disk format drift.
- **Rollback:** revert the PR. Since no schema/data migration ran, rollback needs no data step. Existing persisted grain state remains valid.
- **Verification gates:** `npm run build` (full solution, `TreatWarningsAsErrors` catches missed cref/references) and `npm test` (server specs, including the new concurrency specs). Each D8 step is green before the next.

## Open Questions

- **Link-time readiness for idle epics (D5 edge):** Is the "all members already complete at link time" edge (D6 risk) acceptable with `AutoMarkDoneIfReadyAsync` as the explicit escape, or does `LinkIssueAsync`/`LinkIssuesAsync` need a one-shot readiness check? Out of scope for this issue; flag to epic #36 if a regression surfaces.
- **Cancelled handler final name:** `EpicCancelledHandler` is the most concise and style-aligned with `EpicAutoDoneHandler` (spec says "drop reconcile, style-aligned"). Confirm `EpicCancelledHandler` vs `EpicCancelledProgressHandler` at implementation time. Default: `EpicCancelledHandler`.
- **`using Orleans.Concurrency` removal:** Drop the `using` in `WorkflowGrain.cs`/`RunnerGrain.cs` only if no other symbol from that namespace remains after the attribute is removed. Verify at edit time (the build will flag a leftover unused `using` under the existing analyzer set if configured).
