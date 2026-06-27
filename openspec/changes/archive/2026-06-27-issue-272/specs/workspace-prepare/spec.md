## ADDED Requirements

### Requirement: Workspace prepare action reconciles git state toward the expected branch

The runner SHALL provide a `mohist/workspace-prepare` action that, given a workspace path and an expected branch, reconciles the workspace's local git state to a clean, aligned, ready-to-execute condition before any business task of a stage runs. The action SHALL be idempotent: when invoked on an already-clean workspace that is on the expected branch with no residual git operation in progress, it SHALL complete successfully without modifying the workspace. The action MUST NOT create or clone workspaces, MUST NOT perform any network operation (fetch, pull, push), and MUST NOT change which branch is considered expected — it reconciles local git state toward the declared expected branch only.

#### Scenario: Action receives workspace path and expected branch

- **WHEN** the `mohist/workspace-prepare` action is dispatched
- **THEN** it SHALL receive the workspace path and the expected branch (`workspace.branch`) as inputs
- **AND** it SHALL resolve every git operation relative to that workspace path

#### Scenario: Fast-pass on an already-clean workspace

- **WHEN** the workspace HEAD is on the expected branch, the working tree and index are clean, and no rebase/merge/cherry-pick is in progress
- **THEN** the action SHALL complete successfully without modifying the workspace
- **AND** SHALL complete in under one second

#### Scenario: Abort residual rebase

- **WHEN** the workspace has an in-progress rebase (`.git/rebase-merge` or `.git/rebase-apply` is present)
- **THEN** the action SHALL run `git rebase --abort`
- **AND** SHALL perform no other recovery step until no rebase is in progress

#### Scenario: Abort residual merge

- **WHEN** the workspace has an in-progress merge (`.git/MERGE_HEAD` is present)
- **THEN** the action SHALL run `git merge --abort`

#### Scenario: Abort residual cherry-pick

- **WHEN** the workspace has an in-progress cherry-pick (`.git/CHERRY_PICK_HEAD` is present)
- **THEN** the action SHALL run `git cherry-pick --abort`

#### Scenario: Checkout expected branch when detached or elsewhere

- **WHEN** the workspace HEAD is not on the expected branch (detached HEAD or checked out to a different ref) and no residual rebase/merge/cherry-pick is in progress
- **THEN** the action SHALL run `git checkout <expected branch>`

#### Scenario: Discard uncommitted changes and untracked files

- **WHEN** the working tree or index has uncommitted changes after residual operations have been aborted
- **THEN** the action SHALL run `git reset --hard HEAD`
- **AND** SHALL run `git clean -fd` to remove untracked files and directories
- **AND** SHALL leave the working tree clean

#### Scenario: Health verification gates success

- **WHEN** the action has performed its cleanup steps
- **THEN** the action SHALL verify that HEAD is on the expected branch, that the working tree is clean, and that neither `.git/rebase-merge` nor `.git/rebase-apply` exists
- **AND** SHALL succeed only when all three conditions hold

### Requirement: Workspace prepare failures emit structured diagnostics

When any step of `mohist/workspace-prepare` fails — a git command returns a non-zero exit code, or the final health verification is unmet — the action SHALL fail the task and emit a diagnostic output that identifies the failing step and captures the workspace state at failure time. The output SHALL include a `failureKind` classifying the failure, the expected branch, the current HEAD (commit hash and, if resolvable, its ref name, or `(detached)` when detached), and the residual-state probe results (presence of `rebase-merge`, `rebase-apply`, `MERGE_HEAD`, `CHERRY_PICK_HEAD`, and the `git status --porcelain` output). The action MUST NOT attempt recovery beyond the defined cleanup steps; it SHALL surface the failure so that a rerun starts the next attempt from a fresh workspace-prepare.

#### Scenario: Failed git command reports failureKind and failing step

- **WHEN** a git command invoked by `mohist/workspace-prepare` returns a non-zero exit code
- **THEN** the task SHALL fail
- **AND** the output SHALL include a `failureKind`, the name of the failing step, the expected branch, and the current HEAD
- **AND** the output SHALL include the residual-state probe results captured at the time of failure

#### Scenario: Unmet health verification reports residual state

- **WHEN** the final health verification detects that HEAD is not on the expected branch, the working tree is dirty, or a rebase directory still exists
- **THEN** the task SHALL fail
- **AND** the output SHALL include a `failureKind` identifying the unmet condition
- **AND** the output SHALL include the current HEAD, the expected branch, and the residual-state probe results

### Requirement: Workspace prepare is the first task of every stage in supported profiles

The `mohist/local` and `mohist/github-pr` workflow profiles SHALL declare a `mohist/workspace-prepare` task as the first entry of every stage's task list, ahead of all business tasks. The task SHALL execute exactly once at stage initialization, before any business task or stage check of that stage. The workspace-prepare task SHALL NOT be re-injected before each task within a stage, and SHALL NOT be injected into recovery task sequences (`onFailure` / check repair paths); recovery tasks rely on the workspace state left by the preceding task together with the executor's `enforceCleanWorktree`, not on a fresh prepare.

#### Scenario: First task executed of every stage

- **WHEN** a stage of `mohist/local` or `mohist/github-pr` begins executing its task list
- **THEN** the first task executed SHALL be `mohist/workspace-prepare`
- **AND** no business task of that stage SHALL run before workspace-prepare has completed successfully

#### Scenario: Runs once per stage, not per task

- **WHEN** a stage contains more than one business task
- **THEN** `mohist/workspace-prepare` SHALL execute exactly once at the start of that stage
- **AND** SHALL NOT execute again between business tasks of the same stage

#### Scenario: Recovery sequences are not preceded by a fresh prepare

- **WHEN** a task fails and the profile injects recovery tasks (`onFailure`) or check repair tasks
- **THEN** the recovery sequence SHALL NOT be preceded by a fresh `mohist/workspace-prepare` task
- **AND** the workspace state produced by the failing task together with `enforceCleanWorktree` SHALL be preserved for the recovery tasks
