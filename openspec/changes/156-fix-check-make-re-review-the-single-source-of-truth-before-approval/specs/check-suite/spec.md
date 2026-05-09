## ADDED Requirements

### Requirement: Snapshot-bound approval truth

Mohist SHALL bind the authoritative check-stage AI review result to the code snapshot that is eligible for approval. A check-stage approval request SHALL only be created when the latest AI review PASS, review artifacts, CheckSuite snapshot, approval output, and current worktree snapshot are converged.

#### Scenario: PASS review is bound to current HEAD

- **WHEN** re-review returns PASS after auto-fix
- **THEN** the authoritative `ai-review` output SHALL include the reviewed snapshot SHA
- **AND** `CheckSuite.snapshotSha` SHALL match that reviewed snapshot SHA when an active CheckSuite exists
- **AND** approval output SHALL include the same snapshot SHA

#### Scenario: Dirty worktree is committed before approval

- **WHEN** re-review returns PASS
- **AND** auto-fix or review artifact generation leaves worktree changes
- **AND** Mohist can create a convergence commit successfully
- **THEN** the approval snapshot SHALL be the new committed HEAD
- **AND** the authoritative AI review output and CheckSuite snapshot SHALL be updated to that committed snapshot
- **AND** user approval MAY be requested

#### Scenario: Dirty worktree blocks approval when commit fails

- **WHEN** re-review returns PASS
- **AND** auto-fix or review artifact generation leaves worktree changes
- **AND** Mohist cannot create a clean convergence commit
- **THEN** user approval SHALL NOT be requested
- **AND** the check result SHALL explain that uncommitted auto-fix or review artifact changes blocked approval

#### Scenario: No contradictory current check cycle truth

- **WHEN** a check cycle reaches a terminal PASS, FAIL, or awaiting-approval state
- **THEN** it SHALL NOT expose a current `ai-review` FAIL together with current approval output PASS
- **AND** it SHALL NOT expose approval output for a different snapshot than the authoritative AI review snapshot
