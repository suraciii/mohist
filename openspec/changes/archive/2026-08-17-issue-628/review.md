# Review — Issue 628: Workflow rebase recovery must restore the expected branch

Reviewed commits: T-001..T-005 (`90039e8af`..`6bd8df5be`) against `f9561464e`
(the approved plan) and `master`. Scope: the 5 task commits only; files under
`openspec/changes/issue-628/` are this workflow's own plan artifacts, not
product deliverables.

## Issue acceptance criteria (re-read before the diff)

From the issue body:
1. workspace-prepare / rebase recovery must finish on the expected run branch,
   or fail before the task-completion boundary with a durable actionable error.
2. Rebase conflict resolution must not report success while detached, and must
   preserve the workspace/branch identity needed for a later retry.
3. Add a deterministic fake-worktree regression for detached HEAD, successful
   checkout, conflict state, and idempotent rerun.
4. Do not infer or replay Agent results, change Runner slot policy, or restore
   per-work resource limits.

(Plus the issue comments requirement, captured in the spec: a durably Blocked
settlement releases Runner `activeWorks` / capacity / missing-redelivery at one
exactly-once projection boundary while preserving identity for a matching late
authoritative report, and fences stale `unknown` observations.)

## Verdict

**PASS** — no must-fix problems found. The change is ready to merge.

## Dimension-by-dimension sweep

### Coverage — checked, no issue
- AC1 (finish on expected branch or fail durably before completion):
  `evaluateWorkspaceHealth`/`workspaceHealthDiagnostic`
  (`runtime/workspace-health.ts`) is the single shared invariant; `mohist/rebase`
  verifies it after rebase and after squash (`verifyRebaseCompletion` in
  `actions/rebase.ts`), `mohist/workspace-prepare` runs the full probe→abort→
  reset/clean→checkout→verify machine (`actions/workspace-prepare.ts`), and the
  executor's end-boundary probe (`branch-stability.ts` `checkBranchStability`)
  runs before artifact upload and worktree settlement.
- AC2 (no success while detached, identity preserved): detached / wrong-branch /
  probe-failure / residual-at-end all become `branch-invariant-violation`
  failures with no `addTasks`; the executor bypasses `tryRecovery` for
  `error.code === 'branch-invariant-violation'` at both the action-failure and
  end-boundary sites (`runtime/executor.ts` `isBranchInvariantViolation`).
  Ordinary `conflict` failures remain eligible for recovery (verified by the
  `keeps ordinary conflict failures eligible` test).
- AC3 (fake-worktree regression): `tests/support/fake-worktree.ts`
  (`StatefulFakeWorktree`) drives detached-HEAD, successful checkout, conflict
  state, transient/persistent injection, and idempotent-rerun scenarios across
  `workspace-prepare.spec.ts`, `rebase.spec.ts`, `workspace.spec.ts`,
  `executor-workspace-boundary.spec.ts`, and `executor-recovery.spec.ts`.
- AC4 (no Agent-result replay / slot-policy change / resource-limit restore):
  no new result-replay protocol; `WorkflowRunQuerier` filters only the
  `AttentionStatus` projection; the T-005 test asserts
  `runner.GetSlotsAsync()` is unchanged; no per-work resource limits were
  reintroduced (this branch builds on `bb0e0a1f2` which removed them).

### Correctness — checked, no issue
- The `expectedBranch` for rebase is engine-sourced from `workspace.branch`
  (manifest `engineSource`), never substituted from `baseBranch`; when
  `workspace.branch` is null the action fails with `invalid-input` and a clear
  message rather than degrading to `baseBranch` (`actions/rebase.ts`).
- The completion invariant requires exactly the expected branch + clean +
  non-residual; a successful rebase that leaves detached/wrong-branch/dirty/
  residual state returns `branch-invariant-violation` with the shared
  expected/observed/operation diagnostic and never exposes successful rebase
  output. Conflicts still return the existing `conflict` failure with file
  names and `rebaseLeftInProgress: true` (the resolver's state is not cleaned
  by the action).
- Boundary semantics are coherent: start rejects detached/mismatch/probe
  failure but deliberately allows residual state (so `workspace-prepare` /
  rebase-abort can run); end additionally rejects residual; dirty is deferred
  to `enforceCleanWorktree` at both boundaries.
- T-005: `BlockUnresolvedAgentResult` → `CommitAsync` → `WorkflowRunStore
  .SaveAsync` sets `AttentionStatus = HasBlockedAgentResult() ? "blocked" : null`
  in the same durable commit; `CountRunningAssignedToAsync` /
  `FindRunningAssignedToAsync` / `DispatchService.AddMissingRedeliveriesAsync`
  / `RunnerGrain` activeWorks all funnel through that same row filter, so the
  release is exactly-once. `WorkflowReportService.ReportAsync` now returns
  `stale` for a stale `unknown` observation and never forwards
  `InboundReport.Unknown.Fallback` to `ReceiveTaskReportAsync` (the old code
  forwarded it on Stale — the exact defect this issue's comments call out).
  `RecordObservation` rejects writes on a Blocked settlement, so the blocked
  domain returns `Stale` for a matching late `unknown`.
- Non-Git workspace with an expected branch now fails closed as a probe
  failure, while actions with no expected branch keep the observational path
  (`readCurrentBranch`), preserving pre-change behavior for those actions.

### Consistency with the surrounding codebase — checked, no issue
- The shared health model lives in `runtime/workspace-health.ts` with no git
  adapter; each consumer keeps its own narrow runner (`RunnerGitRunner` /
  `runCommand` / executor `git`), consistent with existing module boundaries.
- Reuses the existing `WorkItemResult` / `ActionError` envelopes, the
  `AttentionStatus` projection, `engineSource` input injection, and existing
  test patterns (`StatefulFakeWorktree`, fake-time server fixtures) rather than
  introducing a parallel protocol or persistence model. The arch-test comment
  reference baseline was updated for the new T-005 doc comments (same ratchet
  pattern as prior issues).
- Existing consumers adjusted legitimately: `runner-host-*` and
  `executor-raw-with` tests now mock a `null` workspace branch (or a clean
  `main` git state) because they exercise log/opencode plumbing, not branch
  stability; `worktree-cleanup-delivery` / `workspace-prepare-workflow` stubs
  answer the new probes.

### Tests — checked, no issue
- Runner: full suite green — 155 files / 1738 tests; focused suites for
  rebase, workspace-prepare, workspace-manager, boundary, recovery, and
  git-action-contracts all green; `tsc` (src + tests) and
  `check:test-boundaries` pass (re-verified).
- Server: `Mohist.Server.SpecTests` 3879/3879 pass (incl. the three new
  `AgentResultSettlementSpecs` fake-time scenarios, three new
  `WorkflowRunQuerierSchedulingSpecs` DB filters, and the updated
  `DispatchServiceReconciliationSpecs`), `Mohist.Server.ArchTests` 68/68 pass
  (re-verified).
- The three new settlement tests genuinely assert the exactly-once boundary:
  repeated reminder + poll + status rounds leave the release unchanged, slot
  totals unchanged, matching late authoritative report settles without
  reintroduction, mismatched reports are fenced, and the matching blocked
  `unknown` observation returns `("stale", "Running")` with no event-stream
  growth and no `TaskFailed`.

## Observations (non-blocking)

1. **workspace-prepare action repair is largely redundant with the manager in
   the executor flow.** The runner's implicit `WorkspaceManager.prepare` /
   `reenterRunBranch` repairs a detached/mismatched/residual workspace before
   every dispatch, so the `mohist/workspace-prepare` action usually hits its
   fast path in production; its repair branch is exercised mainly by unit tests
   and by direct invocation. This is per-spec (the manager and the action share
   the health contract) and not a defect, but the two repair paths could drift
   if only one is extended later.

2. **Conflict preservation vs. preparation abort.** The `mohist/rebase` action
   preserves a conflicted rebase for the resolver, but the implicit manager
   prepare (and `workspace-prepare`) abort residual rebase/merge/cherry-pick
   state before the next task runs, so a resolver task dispatched after a
   conflict starts from a clean on-branch workspace rather than the conflicted
   index. This is pre-existing (the old `reenterRunBranch` also aborted on
   checkout failure) and matches the spec's "abort residual operations before
   branch repair", so it is not a regression, but the interaction is worth
   confirming end-to-end with a real recovery run.

3. **`mohist/rebase` now hard-requires `workspace.branch`.** Because
   `expectedBranch` is engine-sourced and the action fails with `invalid-input`
   when absent, any (hypothetical) rebase invocation without a bound workspace
   branch would now fail instead of rebasing. All current profiles run rebase
   inside the workspace where `workspace.branch` is always defined, so this is
   intended per design ("baseBranch can never substitute"), but it is a new
   hard dependency worth documenting for workflow authors.

4. **Dead export.** `verifyRebaseCompleteAction` / `verifyRebaseComplete` in
   `actions/rebase.ts` remain exported but are no longer used by the new
   completion path (only the new `verifyRebaseCompletion` is). Harmless, but
   the legacy function could be removed to avoid two branch-verification
   semantics coexisting.

5. **End-boundary strictness.** The end probe fails any successful action that
   leaves the workspace off `workspace.branch` (including `mohist/opencode`
   sessions that end on a different branch). This is exactly what the issue
   requires, but it tightens behavior for all workspace tasks; workflows that
   intentionally leave the workspace on another branch would now fail closed.

## Re-review note

This is the first review of the implementation (no prior `review.md`), so no
previous findings to verify. No regression from the plan baseline was found.

<promise>PASS</promise>
