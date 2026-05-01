## MODIFIED Requirements

### Requirement: Check stage behavior
The check stage SHALL perform automated testing, archive the Change, and then present human acceptance gate.

#### Scenario: Automated testing
- **WHEN** check stage starts
- **THEN** agent automatically runs:
  - `npm test` (or equivalent)
  - `npm run lint` (or equivalent)
  - Any other validation commands from workflow config
- **AND** reports results in issue comment

#### Scenario: Archive before approval
- **WHEN** automated tests pass in check stage
- **THEN** system archives the Change to `openspec/changes/archive/YYYY-MM-DD-{name}/`
- **AND** file changes produced by the archive operation SHALL NOT trigger re-evaluation of checks
- **AND** system then waits for human approval (approval gate)
- **AND** user can:
  - Review all changes
  - Approve to complete (marks issue as done)
  - Or request fixes (loop back to build)

#### Scenario: Archive file changes ignored by check
- **WHEN** the archive operation moves a Change directory from `changes/` to `changes/archive/`
- **THEN** the check runner SHALL NOT re-trigger automated testing
- **AND** the check stage proceeds directly to human acceptance

#### Scenario: Issue archive does not re-archive openspec
- **WHEN** an issue is archived after completion
- **THEN** the system SHALL NOT attempt to move the Change directory again
- **AND** issue archive only marks `archivedAt`, cleans worktree, and cleans checkpoints
