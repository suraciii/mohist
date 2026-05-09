## ADDED Requirements

### Requirement: WorktreeManager 强制 squash merge issue 分支

WorktreeManager SHALL merge every completed issue branch into the configured base branch as exactly one squash commit, without using fast-forward merge as a successful final merge strategy.

#### Scenario: fast-forwardable issue branch still lands as squash commit

- **WHEN** an issue branch is ready to merge and is fast-forwardable from the base branch
- **THEN** WorktreeManager checks out the base branch
- **AND** executes a squash merge of `mo/issue-{N}` into the base branch
- **AND** creates exactly one new base-branch commit for the issue
- **AND** does not execute `git merge --ff-only` as the successful final merge operation

#### Scenario: rebased issue branch lands as squash commit

- **WHEN** an issue branch requires a clean rebase before merging
- **AND** the rebase succeeds or conflicts are resolved successfully
- **THEN** WorktreeManager performs the final base-branch integration as a squash merge
- **AND** creates exactly one new base-branch commit for the issue
- **AND** preserves the issue branch's detailed commit history outside the base branch

#### Scenario: squash merge fails

- **WHEN** the squash merge or squash commit fails
- **THEN** WorktreeManager reports a merge failure with target branch, base SHA, candidate head SHA when available, and an actionable error message
- **AND** the issue worktree is retained for retry or manual inspection

### Requirement: WorktreeManager 生成 issue-level squash commit message

WorktreeManager SHALL generate the squash commit message from issue metadata and optional `tasks.json` data provided by the caller.

#### Scenario: tasks metadata is available

- **WHEN** issue title and parsed tasks are provided to the merge operation
- **THEN** the squash commit subject includes the issue title
- **AND** the squash commit body includes the issue number
- **AND** the squash commit body summarizes tasks in stable task order

#### Scenario: tasks metadata is unavailable

- **WHEN** parsed tasks are missing, empty, or unavailable to the caller
- **THEN** the squash commit message still includes the issue title and issue number
- **AND** the merge does not fail solely because task metadata is unavailable

### Requirement: WorktreeManager merge results do not expose fast-forward status

Successful WorktreeManager merge results SHALL identify the landed squash commit without exposing `fastForward` as a result field.

#### Scenario: mergeApprovedCandidate succeeds

- **WHEN** `mergeApprovedCandidate()` successfully merges an approved candidate
- **THEN** the success result includes target branch, base SHA, candidate head SHA, and landed SHA
- **AND** the success result does not include `fastForward`

#### Scenario: integrate-stage records merge success

- **WHEN** the integrate stage records a successful merge result
- **THEN** emitted output and summaries describe squash merge completion
- **AND** emitted output and summaries do not include `fastForward`
