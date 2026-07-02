## Context

Today there is **no event-driven path** from a workflow run reaching `Completed` to its bound issue flipping to `Done`. The issue only transitions via two fallbacks that this change removes:

- **Lazy read-path reconciliation** — `IssueGrain.GetWorkflowStatusAsync` calls `ReconcileWithWorkflowTerminalStateAsync`, which mutates issue status as a side-effect of a query (CQS violation).
- **Daily background sweep** — `IssueWorkflowReconciliationService` (a `BackgroundService`, registered at `MohistServiceRegistration.cs:74`) walks `InProgress` issues once per **day** and triggers the lazy read path.

Worst case: up to 24h latency, and only if nobody opens the issue. The Web stage bar further compounds the confusion by synthesizing a trailing "Done" stage cell that is not a real pipeline stage.

Existing precedent we mirror:

- `EpicAutoDoneHandler` (`Events/Subscriptions/EpicAutoDoneHandler.cs`) subscribes to `com.mohist.issue.work-completed`, resolves the owning epic via `EpicQuerier.GetEpicIdForIssueAsync`, and dispatches to `IEpicGrain.ReconcileAfterTerminalAsync`. This is the exact shape we need, inverted (workflow-run → issue instead of issue → epic).
- `RunnerWorkflowTerminalStatusHandler` already subscribes to `com.mohist.workflow.run.completed` (via the pipe-separated `[Subscription]` pattern) and parses the run id from the CloudEvent `source`.

Key constraint discovered while grounding: **the `com.mohist.workflow.run.completed` event carries no issue context**. `WorkflowRunCompleted` is `record WorkflowRunCompleted;` (empty payload), and the CloudEvent `source` is `/mohist/workflow-runs/{workflowRunId}` (`WorkflowRunStore.WorkflowEventSource`). The `WorkflowRun` aggregate's `WorkflowRunMetadata` has no `issueId`/`projectId` either. The only place the workflow-run → issue link lives is on the **issue** (`Issue.WorkflowRunId`), persisted as an **indexed computed column** on `IssueRow` (`MohistDbContext`: `HasIndex(e => e.WorkflowRunId)` over `COALESCE(json_extract(State,'$.workflowRunId'), …)`). Issue resolution therefore has to be a reverse DB lookup.

Key constraint on the transport: event delivery is **best-effort in-memory**. `WorkflowRunStore.SaveAsync` publishes inside a `try/catch` that swallows failures, and `InMemoryEventBus` swallows handler exceptions. There is no outbox, no retry, no replay. This is the root of the transition-period risk (see Risks).

## Goals / Non-Goals

**Goals:**
- Issue transitions to `Done` within seconds of its bound workflow run reaching `Completed`, driven purely by the event subscription (verifiable with injectable time, no sweep advancement, no simulated read-path open).
- Make `IssueGrain.GetWorkflowStatusAsync` a pure query (no write side-effect).
- Remove the now-redundant daily sweep and its hosted registration.
- Remove the synthesized "Done" stage cell from all Web stage-progression surfaces; keep `WorkflowStage.Done` enum member.

**Non-Goals:**
- A durable at-least-once event mechanism (transactional outbox + dispatcher + DLQ, or event-store replay) — a separate issue. This change's "remove fallback" stance depends on it eventually landing.
- Handling `failed` / `stopped` terminal states → issue transitions (out of scope).
- New visual expression for the terminal state (keep "Integrate all-green + issue status pill").
- Removing the `WorkflowStage.Done` enum member (widely referenced, harmless).

## Decisions

### Decision 1 — New handler subscribes to `com.mohist.workflow.run.completed`

Add `IssueWorkflowCompletionHandler : ICloudEventHandler` under `Events/Subscriptions/`, decorated `[Subscription(Type = EventCatalog.ReverseDns.WorkflowRunCompleted)]`.

- **Shape**: mirrors `EpicAutoDoneHandler` (terminal event → owning aggregate completion) — inject a querier + `IGrainFactory`, dispatch synchronously, swallow+log exceptions so a handler failure never propagates into the workflow-run commit.
- **Run-id extraction**: mirrors `RunnerWorkflowTerminalStatusHandler` — parse the workflowRunId from the CloudEvent `source` via `WorkflowEventSerializer.ExtractContextFromSource`. On empty source, debug-log and return (same as the existing terminal-status handler).

**Alternatives considered:**
- *Attach `issueId`/`projectId` as CloudEvent extensions and read them in the handler (as `EpicAutoDoneHandler` reads `projectid`/`issueid`).* Rejected: would require plumbing issue context into `WorkflowRunStore.SaveAsync` (which builds envelopes from `WorkflowEvent`s that carry no issue context) or into the `WorkflowRun` aggregate. That is a larger, cross-aggregate change for no correctness gain, given an indexed reverse lookup already exists.
- *Subscribe via the pipe-separated pattern like `RunnerWorkflowTerminalStatusHandler`.* Rejected: that handler serves three terminal types with one instance; we handle exactly one type (`Completed`), so a single-type `[Subscription]` is simpler and self-documents the "only Completed drives issue completion" rule (spec: `issue-workflow-completion`).

### Decision 2 — Resolve the owning issue by reverse DB lookup on the indexed `IssueRow.WorkflowRunId`

Add `IssueQuerier.GetIssueIdForWorkflowRunAsync(string workflowRunId)` returning the `issueId` of the **`InProgress`** issue bound to that run (`SELECT IssueId FROM Issues WHERE WorkflowRunId = @id AND Status = 'inProgress'`). It returns `null` when no in-progress issue is bound, in which case the handler no-ops.

- The lookup rides the existing indexed computed column, so it is a single cheap indexed query — no schema change, no new index.
- **Filter on `Status = 'inProgress'`** (not just `WorkflowRunId == id`). This reuses the sweep's documented wisdom: a `WorkflowRunId` is *preserved* on `Done`/archived issues as historical data, so an unfiltered lookup could match a stale binding. Filtering to `InProgress` also makes the post-`Done` idempotent path explicit at the handler level (lookup returns null → no-op) instead of relying solely on the grain guard.

**Alternatives considered:**
- *Resolve without the status filter and lean entirely on `CompleteWorkAsync`'s guard.* Rejected: it re-introduces the historical-binding footgun and does a needless grain activation on already-terminal issues. The status filter is cheaper and safer.
- *Return `null` if more than one in-progress issue matches (defensive).* Not needed: in practice exactly one in-progress issue owns a given run (the workflow is started from one issue). The existing sweep assumes the same 1:1. We keep `FirstOrDefault` semantics for simplicity.

### Decision 3 — Dispatch to `IIssueGrain(issueId).CompleteWorkAsync(workflowRunId)`

The issue grain is keyed directly by `issueId` (`GrainKey.Issue(issueId) => issueId`), so the handler calls `_grains.GetGrain<IIssueGrain>(issueId).CompleteWorkAsync(workflowRunId)`. Idempotency is inherited unchanged from `CompleteWorkAsync` → `Issue.Complete(workflowRunId)`, which already guards on `Status == InProgress` and matching `WorkflowRunId`; once the issue has left `InProgress`, further deliveries are no-ops.

- **No self-deadlock / no detach needed.** `RunnerWorkflowTerminalStatusHandler` detaches its work onto a background task because it calls *back into a workflow grain* from within a workflow-grain publish call stack (non-reentrant grain → self-deadlock). Our handler calls a **different** grain (`IssueGrain`) from the workflow grain's publish path, exactly as `EpicAutoDoneHandler` calls `EpicGrain` from the issue grain's publish path without detaching. So synchronous dispatch is correct and consistent with the closest analog.

### Decision 4 — Read path becomes a pure query; sweep + lazy reconcile deleted

- `IssueGrain.GetWorkflowStatusAsync`: remove the `await ReconcileWithWorkflowTerminalStateAsync(...)` call and the "if the bus subscription missed the Completed event" comment; it now only queries `_workflowQuerier.GetStatusAsync` and projects. Delete the `ReconcileWithWorkflowTerminalStateAsync` method.
- Delete `IssueWorkflowReconciliationService` (`Issue/Services/`) and its `AddHostedService<IssueWorkflowReconciliationService>()` line (`MohistServiceRegistration.cs:74`).
- Delete the now-dead `IssueWorkflowReconciliationServiceSpecs` test file (do not leave old/new tests co-existing).

### Decision 5 — Web: drop Done from rendered stage lists only

Keep `WorkflowStage.Done` enum; only stop *adding it to rendered lists*:
- `WorkflowView.tsx`: remove `WorkflowStage.Done` from `WORKFLOW_STAGES`; drop the Done branches in `getStageStatus`/`getDefaultStage`.
- `SessionTimeline.tsx`: remove `done` from `stageOrder` and `WORKFLOW_STAGES`.
- `IssueCard.tsx`: drop the `status === Done ? WorkflowStage.Done : …` override (line 135) and the Done label entry.
- `IssueDetailPage.tsx`: drop the `[WorkflowStage.Done]: 'Done'` label entry and the `stage === Done → IssueStatus.Done` derivation.

## Risks / Trade-offs

- `[Best-effort event delivery leaves issues stuck on dispatch/handler failure]` → No automatic fallback remains (sweep + lazy reconcile removed). **Accepted transition-period tradeoff**: a momentary `InMemoryEventBus`/`WorkflowRunStore.SaveAsync` publish failure, or a handler exception, can leave an issue stuck in `InProgress` requiring manual re-trigger (re-emit the completed event or re-run the workflow). Mitigated only by logging at the handler (`LogWarning` on exception, symmetric to `EpicReconcileDispatcher`); full mitigation is the durable-event Non-Goal.
- `[Reverse lookup races with the in-progress→done transition]` → The lookup filters `Status = 'inProgress'`; once `CompleteWorkAsync` persists `Done`, subsequent redeliveries resolve to `null` and no-op. This is the intended idempotent behavior, not a hazard.
- `[Handler exception swallowed hides silent no-progress]` → Consistent with the existing terminal-event handlers and the documented best-effort model. Surfaced via `LogWarning`; not escalated to a thrown exception (would propagate into the workflow-run commit and is explicitly the "swallow" posture of the current bus).
- `[Web: keeping WorkflowStage.Done enum invites re-introduction]` → Spec `web-ui` forbids adding it to any rendered list/order; acceptance criteria covers the four surfaces. Enum retained only to minimize churn across widely-referenced call sites.

## Migration Plan

No schema migration, no public API contract change. Deployment is a single server + web build.

- **Order**: ship server (new handler + deletions) and web (Done removal) together. The new event subscription is the primary path; the deletions remove the redundant fallbacks. There is no window where a transition could be missed by *this* change — the sweep's 24h latency already made it ineffective as a real-time path.
- **Rollback**: revert the commit. Re-introducing the sweep + lazy reconcile restores the old (slow) fallback. No data needs repairing: the deleted sweep owned no persistent state, and `GetWorkflowStatusAsync` regaining its lazy reconcile is a safe superset of the pure-query behavior.
- **Stuck-issue recovery during transition period**: until the durable-event Non-Goal lands, operators re-trigger a stuck issue by re-emitting `com.mohist.workflow.run.completed` or re-running the workflow (which rebinds the run and re-emits on completion).

## Open Questions

- Should the new handler emit a metric/counter on successful transition vs. no-op (for observing the transition-period stuck-issue rate)? Not required by the spec; defer unless an observability need surfaces.
- When the durable-event mechanism lands, should the handler be re-evaluated for at-least-once redelivery semantics? Yes — the status-guarded idempotency is already redelivery-safe, so no change is expected, but worth confirming at that time.
