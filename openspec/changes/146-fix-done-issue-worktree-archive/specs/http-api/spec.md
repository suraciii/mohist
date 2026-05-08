## MODIFIED Requirements

### Requirement: REQ-API-001 Manual merge preserves inspection workspace

The manual merge API SHALL merge successfully without cleaning the issue worktree. Merge state and completion state SHALL remain trustworthy, but cleanup SHALL be deferred to archive.

#### Scenario: Manual merge success keeps worktree
- **WHEN** `POST /api/issues/:number/merge` succeeds
- **THEN** the response SHALL indicate success
- **AND** the issue worktree SHALL still be available
- **AND** cleanup SHALL NOT run as part of the merge response

### Requirement: REQ-API-002 Archive API owns cleanup and warning feedback

Archive APIs SHALL be the explicit boundary for hiding issues and cleaning retained local transient state. Single archive SHALL return backend warning feedback for risky states, while batch archive SHALL skip Done issues that are not confirmed merged.

#### Scenario: Single archive returns false-Done warning
- **GIVEN** an issue is Done or Completed
- **AND** its `mergeState` is not `merged`
- **WHEN** `POST /api/issues/:number/archive` succeeds
- **THEN** the response SHALL include a warning explaining the issue is Done but not confirmed merged

#### Scenario: Batch archive skips false-Done issues
- **GIVEN** a Done issue has `mergeState` that is null or not `merged`
- **WHEN** `POST /api/issues/archive-completed` runs
- **THEN** the issue SHALL NOT be archived
- **AND** the response SHALL include `skipped`
- **AND** the response SHALL include the issue number in `skippedNumbers`
- **AND** the response message SHALL explain skipped issues are not confirmed merged

#### Scenario: Batch archive reports result shape
- **WHEN** `POST /api/issues/archive-completed` completes
- **THEN** the response data SHALL include `archived`, `skipped`, `skippedNumbers`, and `message`

### Requirement: REQ-API-003 Archived issues are hidden by default and retrievable explicitly

Issue list APIs SHALL exclude archived issues by default and SHALL provide explicit access to archived issue history.

#### Scenario: Default list excludes archived issues
- **GIVEN** an issue is archived
- **WHEN** the client requests `GET /api/issues` without archived flags
- **THEN** the archived issue SHALL NOT be returned

#### Scenario: Archived list returns archived issues
- **GIVEN** an issue is archived
- **WHEN** the client requests `GET /api/issues?archived=true`
- **THEN** the archived issue SHALL be returned
