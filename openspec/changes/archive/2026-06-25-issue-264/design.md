## Context

Today archiving a `Done` issue destroys its execution history: `Issue.Archive()` sets `_archivedAt` and then nulls `_activeWorkflowRunId` (`Issue.Transitions.cs:189`). Once archived, the issue can no longer surface its workflow run, and the read model returns a null `workflowRunId`. The root cause is a naming/semantic conflation: the field that records "a workflow run was once bound to this issue" is called `ActiveWorkflowRunId`, so every code path treats *reference exists* as *workflow is currently running*.

The public JSON shape already exposes this as `workflowRunId` (`Issue.cs:78` aliases `ActiveWorkflowRunId` → `WorkflowRunId` without `[JsonIgnore]`, while the `Active*` property is `[JsonIgnore]`). The persistence layer already projects it: `IssueRow.WorkflowRunId` is a stored computed column reading `json_extract(State,'$.workflowRunId')` (`MohistDbContext.cs:236`). So clients already see `workflowRunId`; the problem is purely that the domain clears it on archive and that internal naming/logic conflates presence with active state.

Reconciliation (`IssueWorkflowReconciliationService`) selects candidates with `WorkflowRunId != null` and relies on a per-grain `Status != InProgress` guard (`IssueGrain.cs:447`) to no-op done issues. There is **no queryable archived column** on `IssueRow` (the archived filter in `IssueQuerier.cs:111-114` runs in memory after deserializing `State`), so the sweep cannot currently exclude archived issues at the SQL level.

Reference: proposal (`openspec/changes/issue-264/proposal.md`), specs (`openspec/changes/issue-264/specs/`).

## Goals / Non-Goals

**Goals:**
- Archive/unarchive become pure visibility operations that never touch `workflowRunId` or execution history.
- Rename the domain's internal `ActiveWorkflowRunId`/`_activeWorkflowRunId` to `WorkflowRunId`/`_workflowRunId`; remove the `[JsonIgnore]` alias dance so there is a single property.
- Replace id-presence checks that mean *active/controllable workflow* with explicit status+run-state judgments.
- Stop the background reconciliation sweep from selecting archived issues as stuck-run candidates.
- Keep archived issues fully readable through the existing detail/timeline/artifacts/feedback read paths.

**Non-Goals (per issue):**
- No multi-workflow-run history list.
- No change to workflow execute/stop/retry/rerun product semantics.
- No DB cleanup or historical-data GC; no fixing the standalone Web "Archived" list data-fetch bug unless it blocks acceptance.
- No deletion of issues/runs/events/artifacts/session data.

## Decisions

### Decision 1: Stop clearing the reference in lifecycle transitions
`Issue.Archive()` and `Issue.Close()` stop nulling `_workflowRunId`. Only explicit reset paths (`ClearStoppedWorkflow`, called from `TryReuseActiveWorkflowAsync` when reusing a slot at *start* time on an `InProgress` issue) may clear it.

- **Rationale:** The reference is an execution fact. `ClearStoppedWorkflow` already exists for the one legitimate reset (reusing a stopped run slot during a new start) and is guarded by `Status == InProgress` context.
- **Alternative considered:** Add a separate `_historicalWorkflowRunId` kept across archive while `_activeWorkflowRunId` is cleared. Rejected — duplicates state and the issue explicitly wants one neutral reference.

### Decision 2: Rename to a single `WorkflowRunId` property; drop the alias
Replace `_activeWorkflowRunId` → `_workflowRunId`, make `WorkflowRunId` the single public property (remove `[JsonIgnore]` and the `ActiveWorkflowRunId` property). Update `WorkflowProfileLockedException` wording and all log lines that say "active workflow" to "workflow run reference" where they describe the reference, keeping "active" only where it genuinely describes a running state check.

- **Rationale:** The public JSON key is already `workflowRunId` and the computed column already reads it, so this is an internal-only rename with no external contract change.
- **Migration:** Existing rows serialize the key as `WorkflowRunId`/`workflowRunId`; the computed column already handles both casings, so no data backfill is needed. `[JsonIgnore]` removal is safe because the canonical property already serialized under that name.

### Decision 3: Make "active/controllable workflow" an explicit derived judgment
Introduce a private/derived helper on the grain (e.g. `IsWorkflowControllable`) = `Status == InProgress && workflowRunId != null && run is not stopped/terminal`. Use it in:
- `CancelAsync` (`IssueGrain.cs:295`) — only run the "cannot close while workflow running" status check when the workflow is actually controllable; otherwise proceed to `Close()`.
- `TryReuseActiveWorkflowAsync` — unchanged behavior (already runs only at start), but rename the local.
- Profile-lock guard (`IssueGrain.cs:367`) — **keeps** `workflowRunId != null` as the "has started" signal. This is correct: profile is an execution *template* locked once the issue has ever started; a `Done`/archived issue has started and stays locked. The acceptance targets *active-workflow* equivalence (control/retry/recovery/reconciliation), not the has-started lock.

- **Rationale:** Distinguishes "has started" (reference presence, used for template lock) from "is active" (status+run-state, used for control). The issue's non-goal "don't change workflow execution semantics" means the profile lock behavior is preserved.
- **Alternative considered:** Add a persisted `_hasStartedWorkflow` bool. Rejected — `workflowRunId != null` already encodes "has started" exactly now that it is preserved.

### Decision 4: Exclude archived issues from the reconciliation sweep at SQL level
Add an `IsArchived` stored computed column to `IssueRow` (`json_extract(State,'$.archivedAt') IS NOT NULL`, modeled on the existing `WorkflowRunId`/`ProjectId` computed columns in `MohistDbContext.cs:236`), and change the candidate query to `Where(i => i.WorkflowRunId != null && !i.IsArchived)`.

- **Rationale:** Acceptance requires archived issues not be *scanned* as candidates, not merely no-op'd at the grain. A computed column keeps the filter in SQL (the existing in-memory archived filter in `IssueQuerier` proves the team accepts computed columns over denormalized state) and is consistent with the established pattern. The per-grain `Status != InProgress` guard (`IssueGrain.cs:447`) remains as defense-in-depth for done-but-not-archived issues.
- **Alternative considered:** Filter by deserializing `State` in memory. Rejected — defeats the 500-row bounded query and re-reads JSON the DB can compute. Adding a `Status` computed column to also exclude done issues was considered but is out of scope (acceptance names only archived).

### Decision 5: Read path needs no logic change
`IssueQuerier` already projects `issue.ActiveWorkflowRunId` into the `workflowRunId` response field (`IssueQuerier.cs:408,448`) and already joins timeline/feedback by `issue.WorkflowRunId` (`IssueQuerier.cs:558,579`). Once the domain stops clearing the reference, archived issues automatically surface full history. The only change here is the property rename in the mapper.

## Risks / Trade-offs

- `[Persistence backlog: archived issues now always carry a workflowRunId]` → The `IssueRow.WorkflowRunId` index will grow to cover all done+archived issues instead of only active ones. Mitigation: index size is bounded by total issue count, not active set; acceptable for a single-project local-first system. No GC is in scope (non-goal).
- `[CancelAsync behavior change]` → Removing the unconditional workflow-status check means a `Done` issue with a preserved reference no longer queries workflow status before `Close()`. Mitigation: `Close()` already rejects `Done`/archived (`Issue.Transitions.cs:204`), and a terminal issue has no running workflow to block closing. Covered by a regression test.
- `[Computed column migration]` → Adding `IsArchived` requires a schema recomputation on existing rows. Mitigation: it is a stored computed column over existing JSON; SQLite/EF computes it on migration with no data backfill. Verify the migration applies cleanly in the test harness.
- `[Rename ripple]` → Many test helpers reference `ActiveWorkflowRunId`. Mitigation: `TreatWarningsAsErrors` + existing spec tests will surface every call site; update mechanically.

## Migration Plan

1. Domain rename + transition fix (Decisions 1–2) — internal, no external contract change.
2. Add `IsArchived` computed column + EF migration (Decision 4).
3. Adjust control paths and reconciliation query (Decisions 3–4).
4. Add/adjust tests: archive-preserves-reference, unarchive-no-restore-needed, archived-issue-not-swept, cancel-on-done-with-reference, archived-detail-returns-history.
5. **Rollback:** revert is safe — re-clearing the reference on archive restores prior behavior with no schema inconsistency (the `IsArchived` column is purely additive and ignored by old code). No data was destroyed or migrated destructively.

## Open Questions

- Should the profile-lock guard eventually distinguish "started but done" (unlockable) from "started and active"? Out of scope here (non-goal: no execution-semantics change), but worth tracking as a follow-up once multi-run history lands.
- Whether to also add a `Status` computed column to exclude done-but-not-archived issues from the sweep — deferred; the grain guard already handles it and only archived is named in acceptance.
