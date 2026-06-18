## MODIFIED Requirements

### Requirement: Read-only squash mergeability preflight

`WorktreeManager` SHALL provide a mergeability preflight that verifies whether an issue candidate can be squash-merged into the current base branch using the same merge strategy as Integrate, without mutating the base branch, the issue branch, or the workflow workspace branch context. The preflight SHALL be ref-safe: it SHALL NOT check out the base branch inside the workflow workspace, and it SHALL leave the workflow workspace on its `workspace.branch`. Any branch-context-changing work the preflight needs SHALL happen in an isolated temporary workspace separate from the workflow workspace.

#### Scenario: Clean candidate reports structured mergeability

- **GIVEN** a base branch and issue candidate that can be cleanly merged with `git merge --squash <candidate>`
- **WHEN** Mohist checks squash mergeability
- **THEN** the result SHALL include `kind: "merge-ready"`, `strategy: "squash"`, `targetBranch`, `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `canMerge: true`, `conflictFiles`, and `checkedAt`
- **AND** the base branch, issue branch, and workflow workspace branch refs SHALL remain unchanged

#### Scenario: Conflicting candidate reports conflict files

- **GIVEN** a base branch and issue candidate that would fail `git merge --squash <candidate>`
- **WHEN** Mohist checks squash mergeability
- **THEN** the result SHALL have `canMerge: false`
- **AND** the result SHALL include structured conflict file evidence gathered before cleanup
- **AND** cleanup failure SHALL NOT turn a detected conflict into a passing result

#### Scenario: Preflight does not check out the base branch in the workflow workspace

- **WHEN** the mergeability preflight runs against an active workflow workspace
- **THEN** the preflight SHALL NOT run `git checkout <baseBranch>` inside the workflow workspace
- **AND** the workflow workspace SHALL remain on `workspace.branch` before and after the preflight
- **AND** any temporary checkout the preflight needs SHALL happen in an isolated workspace separate from the workflow workspace

## ADDED Requirements

### Requirement: Isolated temporary landing workspaces for branch-stable delivery

`WorktreeManager` SHALL support creating isolated temporary landing workspaces, separate from the workflow workspace, so that delivery operations which need to construct or advance a commit on the base branch can do so without switching the workflow workspace off its run branch. An isolated landing workspace SHALL be materialized as a `git clone --shared` of the workflow workspace (so the run branch's prepared commits are visible alongside the base branch), SHALL be disposable after the delivery operation without affecting the workflow workspace's object store or refs, and SHALL NOT alias the workflow workspace path. The workflow workspace SHALL remain on `workspace.branch` for the lifetime of any landing workspace it spawns.

#### Scenario: Publish lands via an isolated temporary landing workspace

- **WHEN** `integrate:publish` needs to construct the single landing commit on the base branch
- **THEN** WorktreeManager SHALL provide an isolated temporary landing workspace separate from the workflow workspace
- **AND** the landing commit, fast-forward, and push SHALL be performed in that isolated workspace
- **AND** the workflow workspace SHALL remain on `workspace.branch` throughout the publish task

#### Scenario: Landing workspace does not disturb the workflow workspace

- **WHEN** an isolated temporary landing workspace is created and later removed for a delivery operation
- **THEN** the workflow workspace path, branch, and working tree SHALL be unaffected
- **AND** the workspace SHALL remain on `workspace.branch` before, during, and after the landing workspace's lifetime
