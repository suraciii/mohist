## ADDED Requirements

### Requirement: Build-stage orphan recovery inspects tasks.json

When `recoverIssues()` encounters an active issue at `stage=build` that is not in `awaiting` approval state, the system SHALL read the tasks.json file from the issue's openspec change directory and determine recovery action based on task completion status.

#### Scenario: All tasks pass — auto-advance to review

- **WHEN** `recoverIssues()` finds an active issue with `stage=build` and `approvalState.status !== 'awaiting'`
- **AND** the issue has an openspec change directory containing a valid tasks.json
- **AND** all tasks in tasks.json have `passes=true`
- **THEN** the system SHALL update the issue stage to `review`
- **AND** the issue status SHALL remain `active`
- **AND** the system SHALL NOT set the issue to `blocked`

#### Scenario: Partial tasks pass — blocked with progress summary

- **WHEN** `recoverIssues()` finds an active issue with `stage=build` and `approvalState.status !== 'awaiting'`
- **AND** the issue has an openspec change directory containing a valid tasks.json
- **AND** at least one task in tasks.json has `passes=false`
- **THEN** the system SHALL set the issue status to `blocked`
- **AND** the system SHALL write a progress summary to the recovery log, indicating how many tasks passed out of total and which tasks are pending (e.g. "2/3 tasks completed, T-003 pending")

#### Scenario: No tasks.json found — fallback to blocked

- **WHEN** `recoverIssues()` finds an active issue with `stage=build`
- **AND** no openspec change directory or tasks.json can be found for the issue
- **THEN** the system SHALL set the issue status to `blocked`
- **AND** log that recovery could not determine build progress due to missing tasks.json

#### Scenario: Invalid tasks.json — fallback to blocked

- **WHEN** `recoverIssues()` finds an active issue with `stage=build`
- **AND** the tasks.json file exists but contains invalid JSON or is missing the `tasks` array
- **THEN** the system SHALL set the issue status to `blocked`
- **AND** log that recovery failed due to malformed tasks.json

### Requirement: Non-build-stage orphan recovery preserves existing behavior

For active orphan issues at stages other than `build`, `recoverIssues()` SHALL follow the existing logic without change.

#### Scenario: Awaiting approval — restore pending gate

- **WHEN** `recoverIssues()` finds an active issue with `approvalState.status === 'awaiting'`
- **THEN** the system SHALL restore the pending gate in memory
- **AND** the issue status SHALL remain `active`

#### Scenario: Plan stage orphan — blocked

- **WHEN** `recoverIssues()` finds an active issue with `stage=plan` and `approvalState.status !== 'awaiting'`
- **THEN** the system SHALL set the issue status to `blocked`
- **AND** clear the approval state
