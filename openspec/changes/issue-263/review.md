# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:173,180`
  Evidence: `StartAsync` called `TryStartNextAsync` whenever `row.Status == "running"`, including when `domain.Start()` was a no-op on an already-running epic. This violated the spec that repeated Start on a running epic SHALL NOT re-trigger advancement (`specs/epic-lifecycle/spec.md:131`).
  Verification: Added `var wasAlreadyRunning = row.Status == EpicStatusName.Running` guard before `domain.Start()`, and gate `TryStartNextAsync` on `!wasAlreadyRunning`. `dotnet test --filter EpicProgressionSpecs` — 13 passed.
  Status: resolved

- [ID: item-2]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:208,215`
  Evidence: `ResumeAsync` had the same no-op/side-effect mismatch: called `ReconcileAfterTerminalInternalAsync` even when `domain.Resume()` was a no-op on an already-running epic. Violated spec that Resume on already-running SHALL NOT re-trigger advancement (`specs/epic-lifecycle/spec.md:168`).
  Verification: Added `var wasAlreadyRunning = row.Status == EpicStatusName.Running` guard before `domain.Resume()`, and gate `ReconcileAfterTerminalInternalAsync` on `!wasAlreadyRunning`. `dotnet test --filter "EpicProgressionSpecs|EpicAutoDoneSpecs"` — 25 passed.
  Status: resolved

- [ID: item-3]
  Severity: info
  Scope: `packages/web/src/entities/epic/api/queries.test.tsx:57`
  Evidence: `resumeEpic` mock resolved to `status: 'active'` which is a legacy value; Resume now transitions to `running`. Test assertion unaffected (only checks function invocation), but stale mock data invites confusion.
  Verification: Changed mock to `status: 'running'`. `npm run test:run -w packages/web -- queries` — 159 passed.
  Status: resolved

## Blocking Items

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicProgressionSpecs.cs`
  Evidence: `StartAsync_AlreadyRunningEpic_IsIdempotentAndDoesNotReStart` (line 63) seeds an in-progress linked issue whose presence blocks `TryStartNextAsync`. The idempotent Start test thus passes even without the `wasAlreadyRunning` guard because the serial slot is occupied. No test covers an already-running epic WITHOUT an in-progress issue, where repeated Start could still reach `TryStartNextAsync` selection (the running-but-idle case). Similarly `ResumeAsync_AlreadyRunningEpic_IsIdempotentAndDoesNotReAdvance` (line 329) has the same blind spot.
  SuggestedAction: Add a grain spec: running epic, no in-progress issue, one startable linked issue — calling Start twice must only call `IIssueGrain.StartWorkAsync` once (the first call). Add a matching Resume spec.
  Verification: `dotnet test --filter EpicProgressionSpecs` after adding tests.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Epic/Grain/EpicProgressionSpecs.cs`
  Evidence: No direct Orleans-concurrency test verifies that `PauseAsync` preempts an in-flight `ReconcileAfterTerminalAsync` turn. AC #7 requires Pause wins over a terminal-event-triggered auto-advance. The design relies on Orleans turn-based serialization (D2), but the grain spec layer does not simulate concurrent `PauseAsync` + `ReconcileAfterTerminalAsync` calls to prove that no issue starts after Pause takes effect.
  SuggestedAction: Add a test that issues a `PauseAsync` timestamped after a `ReconcileAfterTerminalAsync` has begun its DB read but before `TryStartNextAsync` reaches `StartWorkAsync`, and asserts no issue start. If the current design does not support preemption mid-turn, document the partial-turn race window explicitly and add an explicit in-flight check in `TryStartNextAsync`.
  Verification: `dotnet test --filter EpicProgressionSpecs` after adding the concurrency test.
  Status: open

## Follow-up Items

- [ID: item-6]
  Severity: follow-up
  Scope: `packages/web/src/entities/epic/api/queries.ts:155`
  Evidence: `useStartEpic` and `useResumeEpic` (line 139) invalidate `['epics']` and `['epics', projectId, id]` but not `['issues']`. Both server actions can start a linked issue via `IIssueGrain.StartWorkAsync` (`EpicGrain.cs:393`), changing issue workflow state. The manual `useStartIssue` path invalidates `['issues']` (`queries.ts:80`), so Start Epic / Resume can leave the issue-list cache stale after advancing work.
  SuggestedAction: Invalidate `['issues']` after Start Epic and Resume success, matching the `useStartIssue` pattern. If `StartWorkAsync` is part of the lifecycle surface, the frontend must mirror the invalidation.
  Status: follow-up

- [ID: item-7]
  Severity: follow-up
  Scope: `packages/web/src/pages/dashboard/productivity/EpicProgressList.tsx:29`
  Evidence: `isInProgressEpic` groups `idle` and `running` epics under "In-progress Epics," but the new status model defines idle as "exists, not yet started" — not in-progress. The detail page already differentiates idle (Start Epic action) from running (Pause action). The dashboard widget also omits `nextIssueReason`, `activeIssues`, and `blockedIssues` from its row display, limiting the running-but-idle/blocked observability called for by AC #8/#9.
  SuggestedAction: Separate idle from running in the dashboard label, or add a distinct "Active epics" section. Surface `nextIssueReason` for running epics without a next issue, and show blocked/active counts.
  Status: follow-up

- [ID: item-8]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/IEpicGrain.cs:15`
  Evidence: `AutoMarkDoneIfReadyAsync` remains on the public grain interface while all event wiring and the sweep now call `ReconcileAfterTerminalAsync`. The narrower entry point skips running-epic advancement and exists only as a backward-compatible path. No current callers outside legacy code use it, but its public visibility could mislead future consumers.
  SuggestedAction: Remove `AutoMarkDoneIfReadyAsync` from `IEpicGrain` and the grain implementation once all callers are confirmed migrated, or mark it `[Obsolete]`.
  Status: follow-up

- [ID: item-9]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Data/Epic/EpicRow.cs:11`
  Evidence: `EpicRow.Status` defaults to the legacy string `"active"`. The create path overwrites it via `MapToRow`, so production rows are correct. However, any test seed, fixture, or future persistence path that constructs an `EpicRow` without explicitly setting `Status` can reintroduce a legacy value post-migration.
  SuggestedAction: Change the entity default from `"active"` to `"idle"`.
  Status: follow-up

- [ID: item-10]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Events/Hosting/EpicReconciliationService.cs:152`
  Evidence: The sweep default period was shortened from 24h to 10 minutes for `running` epics. This increases DB load by ~144x for the candidate query. While the candidate set is small and indexed, the operational impact is worth noting. The design Open Question Q1 suggested a shorter cadence specifically; the 10-minute choice is reasonable but could be tuned further based on observed load.
  SuggestedAction: Monitor DB load in production; consider a separate cadence for idle vs. running candidates if needed (e.g., 10m for running, 24h for idle).
  Status: follow-up

- [ID: item-11]
  Severity: follow-up
  Scope: `packages/web/src/pages/epics/ui/EpicListPage.tsx:78`
  Evidence: The epic list page still renders a per-card `epic-card-start` action for `progress.nextIssue` (manual issue-start path). Issue #263 requires removing only the detail-page `epic-detail-next-start`, so this is not a spec violation. However, it creates two different "start next" affordances across list/detail surfaces after the detail page moved to epic-level Start.
  SuggestedAction: Consider whether the list-page card Start should also be demoted or relabeled for consistency with the new epic-level Start/Pause/Resume model.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-12]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Epic/Grains/EpicGrain.cs:432`
  Evidence: Auto-done treats an epic with zero linked issues as ready because `ComputeUndeliveredLinkedNumbersAsync` returns an empty set (`undelivered.Count == 0`). The read model `EpicProgress.Build` at `EpicProgress.cs:37` requires `linked.Count > 0` for `readyToMarkDone`. This inconsistency predates issue #263 (the existing `AutoMarkDoneIfReadyAsync` had identical logic) and is locked in by `AutoMarkDoneIfReadyAsync_NoLinkedIssues_TransitionsToDone` at `EpicAutoDoneSpecs.cs:156`. In practice, an empty-linked-issue epic never triggers a terminal event, so the sweep is the only path that hits this.
  SuggestedAction: Align grain readiness with the read model by requiring at least one linked issue, or explicitly accept empty-epic auto-done as intentional.
  Status: pre-existing

- [ID: item-13]
  Severity: info
  Scope: `packages/web/src/entities/epic/model/types.ts:14`
  Evidence: `parseEpicStatus` handles legacy `'active'` → `Idle`, but the raw API response strings are compared directly in components (e.g., `epic.status === EpicStatus.Idle` at `EpicDetailPage.tsx:450`). A non-migrated `'active'` value after migration would not match `'idle'`. After the EF migration backfills all rows to `'idle'`, this is not a live risk. However, the normalization path is inconsistent: the server parses `'active'` defensively in `EpicStatusName.Parse`, but the web component comparisons bypass `parseEpicStatus`.
  SuggestedAction: Either normalize status at the server DTO boundary before serialization, or apply `parseEpicStatus` in the web client at the point of data ingestion.
  Status: pre-existing

<promise>PASS</promise>
