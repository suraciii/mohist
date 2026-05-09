## ADDED Requirements

### Requirement: Check approval truth convergence

Check-stage issue detail and approval APIs SHALL expose and enforce the latest authoritative, snapshot-bound AI review truth. The API SHALL NOT allow approval to advance when the current worktree, CheckSuite state, approval output, or latest AI review verdict disagree.

#### Scenario: Issue detail shows latest re-review PASS

- **WHEN** an AI review FAIL is fixed
- **AND** regenerated re-review returns PASS
- **THEN** `GET /api/issues/:number` SHALL expose the latest `ai-review` PASS as current truth
- **AND** it SHALL NOT show the earlier FAIL as the active gate result

#### Scenario: CheckSuite API exposes matching AI review snapshot

- **WHEN** `GET /api/issues/:number/check-suite` returns an active CheckSuite
- **AND** `checks['ai-review'].output.snapshotSha` is present
- **THEN** that snapshot SHA SHALL match `CheckSuite.snapshotSha`

#### Scenario: Approve rejects non-PASS review truth

- **WHEN** the user approves a check-stage issue
- **AND** the latest authoritative AI review verdict is not PASS
- **THEN** the approve endpoint SHALL NOT advance the issue to Integrate
- **AND** it SHALL return a clear error or rerun response

#### Scenario: Approve rejects snapshot drift

- **WHEN** the user approves a check-stage issue
- **AND** approval output snapshot SHA, active CheckSuite snapshot SHA, current worktree HEAD, or worktree cleanliness do not match
- **THEN** the approve endpoint SHALL NOT advance the issue to Integrate
- **AND** it SHALL return a clear error or rerun response

#### Scenario: CLI displays consistent approval truth

- **WHEN** `mo issue show` displays check-stage gate and approval details
- **THEN** the displayed AI review verdict SHALL match the approval output verdict
- **AND** any displayed snapshot metadata SHALL refer to the same approved snapshot
