## MODIFIED Requirements

### Requirement: Read-only squash mergeability preflight

`WorktreeManager` SHALL provide a mergeability preflight that verifies whether an issue candidate can be squash-merged into the current base branch using the same merge strategy as Integrate, without mutating the base branch or the issue branch.

#### Scenario: Clean candidate reports structured mergeability

- **GIVEN** a base branch and issue candidate that can be cleanly merged with `git merge --squash <candidate>`
- **WHEN** Mohist checks squash mergeability
- **THEN** the result SHALL include `kind: "merge-ready"`, `strategy: "squash"`, `targetBranch`, `baseSha`, `candidateHeadSha`, `mergeBaseSha`, `canMerge: true`, `conflictFiles`, and `checkedAt`
- **AND** the base branch and issue branch refs SHALL remain unchanged

#### Scenario: Conflicting candidate reports conflict files

- **GIVEN** a base branch and issue candidate that would fail `git merge --squash <candidate>`
- **WHEN** Mohist checks squash mergeability
- **THEN** the result SHALL have `canMerge: false`
- **AND** the result SHALL include structured conflict file evidence gathered before cleanup
- **AND** cleanup failure SHALL NOT turn a detected conflict into a passing result

### Requirement: Authoritative final squash merge diagnostics

`WorktreeManager` SHALL continue to treat the real Integrate squash merge as the final authority and SHALL report structured conflict evidence when that merge fails.

#### Scenario: Final merge race reports structured conflicts

- **GIVEN** a candidate passed preflight but a later race or Integrate-generated artifact commit introduces a squash merge conflict
- **WHEN** Integrate runs the authoritative `git merge --squash <candidate>` operation
- **THEN** Integrate SHALL fail the merge task
- **AND** the failure output SHALL include `targetBranch`, `strategy`, conflict files, and available `baseSha`, `candidateHeadSha`, and `mergeBaseSha`
