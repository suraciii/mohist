## ADDED Requirements

### Requirement: Merge boundary validates source worktree cleanliness

Before the merge action begins modifying the target branch, the runner SHALL validate that the source worktree is clean. A dirty worktree at the merge boundary SHALL block the merge and produce structured dirty-worktree evidence.

#### Scenario: Clean worktree check at merge boundary

- **WHEN** `mohist/merge` is invoked for an Integrate workflow task
- **THEN** the first validation SHALL be a `git status --porcelain` check in the task workspace
- **AND** the merge SHALL NOT proceed to fetch, checkout, rebase, or push operations if the worktree is dirty

#### Scenario: Dirty worktree at merge boundary produces structured evidence

- **WHEN** `mohist/merge` detects a dirty worktree at the merge boundary
- **THEN** the failure output SHALL include the categorized file lists from `git status --porcelain`
- **AND** the failure SHALL include the phase classification `source-cleanup`
- **AND** the merge action SHALL NOT silently commit the dirty changes

#### Scenario: Merge-boundary clean check is not a stage-level check

- **WHEN** the merge action validates source worktree cleanliness
- **THEN** the validation SHALL execute inside the merge task action
- **AND** it SHALL NOT be modeled as a workflow check, stage gate, or separate approval step
