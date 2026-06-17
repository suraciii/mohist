## MODIFIED Requirements

### Requirement: REQ-WFE-005 Intelligent spec sync resolves obvious delta classification mistakes

The workflow engine SHALL provide an intelligent OpenSpec sync path for `integrate:spec-sync` that can absorb obvious requirement-level delta classification mistakes while preserving strict validation. At minimum, when a `MODIFIED` requirement has no matching source requirement in the main spec, has no rename ambiguity, and does not duplicate an existing target requirement, the sync path SHALL apply it as an added requirement and record the correction. After the intelligent sync writes spec changes to the worktree, the task SHALL commit the changes or report a no-change result, and the runner SHALL verify `git status --porcelain` is clean before marking the task completed.

#### Scenario: Modified requirement is applied as added when source is absent

- **WHEN** `integrate:spec-sync` processes a `MODIFIED` requirement
- **AND** the main spec has no matching source requirement
- **AND** no rename maps to that source
- **AND** the target requirement name does not already exist
- **THEN** the sync SHALL add the requirement to the main spec
- **AND** the sync output SHALL record a correction from `modified` to `added` with capability, requirement, and reason

#### Scenario: Ambiguous or destructive deltas still fail

- **WHEN** `integrate:spec-sync` processes a missing-source `REMOVED` or `RENAMED FROM` requirement
- **THEN** the sync SHALL fail with structured conflict output
- **AND** it SHALL NOT silently delete, rename, or invent source requirements

#### Scenario: Spec-sync cannot complete with uncommitted changes

- **WHEN** `integrate:spec-sync` has written spec changes to the worktree
- **THEN** the task SHALL commit those changes or report that no changes were made
- **AND** the runner SHALL verify `git status --porcelain` is empty before reporting task completion
- **AND** if the worktree remains dirty the task SHALL fail with structured dirty-worktree evidence

## ADDED Requirements

### Requirement: Merge preflight validates clean worktree before delivery

The workflow engine SHALL ensure the merge action validates source worktree cleanliness before executing any delivery side effects. If the source worktree is dirty when merge starts, the merge SHALL fail before fetch, rebase, landing, or push operations begin.

#### Scenario: Dirty worktree blocks merge delivery

- **WHEN** the workflow engine dispatches `integrate:merge`
- **AND** the merge action detects a dirty source worktree
- **THEN** the merge SHALL fail with phase `source-cleanup`
- **AND** no fetch, rebase, landing, or push operations SHALL execute

#### Scenario: Merge phase failures produce distinct evidence

- **WHEN** `integrate:merge` fails
- **THEN** the task result SHALL include a `phase` field identifying the failure as `source-cleanup`, `fetch`, `rebase-conflict`, `landing-validation`, or `push`
- **AND** the WorkflowRun SHALL persist this phase classification as part of the task failure evidence
