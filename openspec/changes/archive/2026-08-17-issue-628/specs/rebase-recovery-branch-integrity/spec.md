### Requirement: Expected run branch is the recovery completion invariant
Rebase conflict recovery and workspace preparation SHALL use the workflow workspace's expected run branch as the authoritative branch identity. The rebase target (`baseBranch` or its remote ref) SHALL NOT substitute for the expected run branch. A recovery attempt SHALL be considered successful only when `HEAD` is attached to exactly the expected run branch, the worktree is clean, and no rebase, merge, or cherry-pick residual state remains.

#### Scenario: Successful recovery is on the expected run branch
- **WHEN** the rebase completes, `HEAD` names the expected run branch, the worktree has no staged, unstaged, or untracked changes, and no residual operation state is present
- **THEN** the runner SHALL report the recovery as successful

#### Scenario: Rebase completion leaves detached HEAD
- **WHEN** the rebase command completes but `HEAD` is detached at a commit
- **THEN** the runner SHALL NOT report successful recovery
- **AND** the task result SHALL be a branch-integrity failure

#### Scenario: Rebase completion is on another branch
- **WHEN** the rebase command completes but the current branch differs from the expected run branch
- **THEN** the runner SHALL NOT report successful recovery
- **AND** the task result SHALL identify the expected branch and the observed branch

### Requirement: Workspace preparation SHALL repair a safe branch mismatch
Workspace preparation SHALL probe the current branch, residual operation state, and worktree status before declaring success. When the expected branch already exists and the workspace can be safely cleaned, preparation SHALL restore a detached or mismatched workspace to that branch, then repeat the health probes before returning success.

#### Scenario: Detached clean workspace can be repaired
- **WHEN** workspace preparation observes detached `HEAD`, no residual operation, and a clean worktree, and the expected run branch exists
- **THEN** it SHALL check out the expected run branch
- **AND** it SHALL report success only after a follow-up probe confirms that branch and a clean, non-residual workspace

#### Scenario: Dirty mismatched workspace is repaired in order
- **WHEN** workspace preparation observes a branch mismatch together with tracked or untracked changes
- **THEN** it SHALL remove residual operation state, reset and clean the worktree as required, and check out the expected run branch
- **AND** it SHALL verify the final branch and clean state before reporting success

#### Scenario: Branch repair cannot be completed
- **WHEN** the expected branch cannot be checked out or the final probe still observes a detached, mismatched, dirty, or residual state
- **THEN** workspace preparation SHALL return a failure
- **AND** it SHALL NOT report the workspace as prepared

### Requirement: Conflict and residual operation states SHALL remain failures until verified clean
A rebase conflict SHALL be reported as a failure with its unresolved conflict information and SHALL NOT be represented as a successful rebase. Workspace preparation SHALL abort residual rebase, merge, and cherry-pick operations before branch repair, SHALL re-probe each aborted state, and SHALL fail when an abort or re-probe cannot establish a clean non-residual workspace.

#### Scenario: Rebase detects unresolved conflicts
- **WHEN** the rebase command fails and Git reports unresolved files
- **THEN** the runner SHALL return a conflict failure that identifies the unresolved files
- **AND** it SHALL NOT return a successful rebase result

#### Scenario: Residual rebase state is cleared before retry
- **WHEN** workspace preparation finds a residual rebase state from an earlier failed attempt and the abort succeeds
- **THEN** it SHALL confirm that the residual state is gone before checking out or validating the expected run branch
- **AND** a subsequent retry SHALL be allowed to start from the preserved workflow workspace

#### Scenario: Residual cleanup cannot be verified
- **WHEN** an abort fails, the residual state remains, or a residual-state probe fails
- **THEN** workspace preparation SHALL return a durable failure
- **AND** it SHALL NOT continue to task completion or report successful recovery

### Requirement: Task boundaries SHALL enforce branch integrity
The runner SHALL validate the expected run branch at the task start and before successful task completion for workflow actions operating in a Git workspace. A detached `HEAD`, a branch mismatch, or an unable-to-probe branch SHALL fail the task at the relevant boundary and SHALL prevent an invalid workspace from being converted into successful task completion.

A `branch-invariant-violation` from an action's final health check or from the executor's end-boundary probe SHALL be reported with `status: failed` and without recovery `addTasks`. The runner SHALL NOT pass that result through `tryRecovery`, because the current server contract maps `completed` to task success and then completes the task and its follow-ups. An explicit later retry MAY repair the preserved workspace, but the failed attempt itself SHALL never settle as successful.

#### Scenario: Task starts detached
- **WHEN** the task start probe observes detached `HEAD` while an expected run branch is defined
- **THEN** the runner SHALL fail the task before invoking the action
- **AND** the failure SHALL identify the expected branch and the observed detached reference when available

#### Scenario: Action leaves the workspace detached
- **WHEN** an action returns successfully but the task end probe observes detached `HEAD`
- **THEN** the runner SHALL fail the task before settling it as successful
- **AND** it SHALL report a branch-integrity failure at the end boundary

#### Scenario: Branch probe fails at a task boundary
- **WHEN** the runner cannot determine the current branch at the start or end boundary
- **THEN** it SHALL fail the task with an actionable branch-probe diagnostic
- **AND** it SHALL NOT treat the missing observation as evidence that the expected branch invariant holds

#### Scenario: End-boundary branch failure cannot be converted by a recovery handler
- **WHEN** an action returns successfully but the end probe reports `branch-invariant-violation` and the work item declares a matching recovery handler or self-retry
- **THEN** the runner SHALL return a failed `WorkItemResult` with the branch diagnostic
- **AND** it SHALL omit `addTasks`
- **AND** the server SHALL translate and persist the report as a failed task without completing the task or inserting follow-ups

#### Scenario: Ordinary conflict recovery remains eligible
- **WHEN** a rebase action returns its unresolved-conflict failure rather than a branch-integrity failure
- **THEN** the existing configured resolver/retry path MAY schedule its follow-up tasks
- **AND** the conflict result SHALL remain a failure until a later task independently succeeds

### Requirement: Durable blocked settlement SHALL release Runner projections at one exactly-once boundary
When an Agent result settlement reaches its deadline, the workflow SHALL durably commit the `Unknown` to `Blocked` transition before the Runner control plane removes that attempt from live projections. The same committed boundary SHALL make the run absent from Runner `activeWorks`, reduce used capacity, and exclude it from missing-redelivery reconciliation. This is a projection release, not a Runner slot-policy change.

Before the deadline, an `Unknown` attempt SHALL remain represented as active work for its assigned Runner and SHALL retain its capacity reservation. After durable `Blocked`, the workflow SHALL retain the assignment and task/work/Runner settlement identity for a matching late authoritative report, while a mismatched report SHALL remain stale. An inbound `unknown` result SHALL remain a non-authoritative observation: when `ObserveAgentResultUnknownAsync` returns `Stale`, report handling SHALL return `stale` and SHALL NOT forward `InboundReport.Unknown.Fallback` to `ReceiveTaskReportAsync`. Repeated reminders, polls, and status reads SHALL not release the same active work more than once.

#### Scenario: Fake-time deadline releases active work and capacity once
- **WHEN** fake time is before the settlement deadline and the attempt is `Unknown`
- **THEN** Runner `activeWorks` SHALL contain the attempt and capacity SHALL count its slot
- **WHEN** fake time reaches the deadline and the workflow durably commits `Blocked`
- **THEN** the same post-commit observation SHALL omit the attempt from `activeWorks`, reduce used capacity by exactly one, and exclude it from `AddMissingRedeliveriesAsync`
- **AND** Runner slot configuration SHALL remain unchanged
- **WHEN** the reminder or poll reconciliation is repeated
- **THEN** the active-work and capacity release SHALL remain unchanged rather than being applied again

#### Scenario: Blocked settlement preserves an identity-matching late report
- **WHEN** a late report carries the original `taskRunId`, `workId`, and assigned `runnerId` after the attempt is durably `Blocked`
- **THEN** the workflow SHALL accept it through the authoritative report path and settle the original attempt
- **AND** it SHALL not require the blocked attempt to be reintroduced into Runner `activeWorks` or capacity

#### Scenario: Blocked settlement fences a mismatched late report
- **WHEN** a late report carries a different task, work, or Runner identity after the attempt is durably `Blocked`
- **THEN** the workflow SHALL reject it as stale
- **AND** it SHALL not clear or revive the blocked settlement or alter Runner projections

#### Scenario: Blocked settlement fences a matching non-authoritative unknown report
- **WHEN** a matching report for a durably `Blocked` attempt has `unknown` status
- **THEN** `WorkflowReportService` SHALL submit it only to the observation path
- **AND** when that observation is rejected as stale, the service SHALL return a stale acknowledgement without forwarding the translator's failed fallback task report
- **AND** the workflow SHALL not emit `TaskFailed`, mutate the blocked settlement, or reintroduce the attempt into Runner `activeWorks`, capacity, or missing-redelivery reconciliation
- **AND** only an explicitly authoritative matching success or failure report MAY enter task settlement

### Requirement: Branch-recovery failures SHALL carry durable actionable diagnostics
When the runner cannot restore the expected branch or establish a clean non-residual workspace, the existing workflow result failure SHALL carry a durable diagnostic that identifies the expected branch, the observed branch or detached reference, the relevant workspace state, and the operation that failed. The diagnostic SHALL be available to an exact retry without relying on inferred action output.

#### Scenario: Checkout failure identifies the branch mismatch
- **WHEN** checkout of the expected run branch fails from a detached or mismatched workspace
- **THEN** the failure diagnostic SHALL identify the expected branch, the observed branch or detached reference, and the checkout failure
- **AND** the result SHALL remain a failure

#### Scenario: Health verification failure identifies residual state
- **WHEN** final verification finds a dirty worktree or residual rebase, merge, or cherry-pick state
- **THEN** the failure diagnostic SHALL identify the failed health condition and its observed state
- **AND** the runner SHALL preserve that failure through normal workflow result reporting

### Requirement: Recovery retries SHALL preserve workspace and run-branch identity
A recovery attempt and an exact retry SHALL continue to use the same workflow workspace path, workflow run identity, expected run branch, and branch reference. Recovery failure SHALL NOT replace the workspace with a new workspace, create a different run branch, or alter the identity binding merely because branch repair failed. Re-running preparation with the same inputs SHALL be idempotent: it SHALL either converge to the expected clean state or return the same class of actionable failure without reporting success.

#### Scenario: Failed repair preserves the retry target
- **WHEN** branch checkout or residual cleanup fails during recovery
- **THEN** the existing workflow workspace and its expected run-branch identity SHALL remain the target of the next attempt
- **AND** the failure SHALL be retryable using the same workspace identity

#### Scenario: Exact retry repairs the preserved workspace
- **WHEN** an exact retry runs after the transient repair failure is removed
- **THEN** it SHALL prepare the same workspace on the same expected run branch
- **AND** it SHALL report success only after the branch and clean non-residual checks pass

#### Scenario: Repeated preparation of a healthy workspace
- **WHEN** preparation is invoked again with the same workflow run and the workspace is already on the expected clean branch
- **THEN** it SHALL preserve the workspace and run-branch identity
- **AND** it SHALL return success without creating a replacement workspace or branch
