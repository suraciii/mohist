# OpenSpec Capability: epic-tracking

### Requirement: Epic Domain Model

System SHALL model an Epic as a named, described, prioritized long-running goal container with `active`, `done`, and `closed` statuses.

#### Scenario: Create active Epic

- **WHEN** a user creates an Epic with title, description, and priority
- **THEN** the system persists the Epic with status `active`
- **AND** the Epic has timestamps suitable for list and detail display

#### Scenario: Epic is not executable work

- **WHEN** the system stores or reads Epics
- **THEN** Epics are separate from issues
- **AND** Epics do not have workflow stage, run state, worktree, branch, task execution, or check execution fields

### Requirement: Primary Epic Issue Membership

System SHALL allow an Epic to link existing issues while enforcing that an issue belongs to at most one primary Epic in the first version.

#### Scenario: Add issue to Epic

- **WHEN** a user adds an existing issue to an existing Epic
- **THEN** the issue appears in the Epic linked issue list
- **AND** the issue workflow state is unchanged

#### Scenario: Remove issue from Epic

- **WHEN** a user removes a linked issue from an Epic
- **THEN** only the Epic membership is removed
- **AND** the issue workflow state, prerequisite data, and worktree data are unchanged

#### Scenario: Reject duplicate primary membership

- **WHEN** a user adds an issue that already belongs to another Epic
- **THEN** the system rejects the add operation with a clear error identifying the existing Epic
- **AND** the existing membership is preserved

### Requirement: Projected Epic Progress

System SHALL project Epic progress from linked issue state at read time rather than storing progress as user-edited data.

#### Scenario: Delivered and total counts

- **WHEN** an Epic is listed or shown
- **THEN** `totalIssueCount` equals the number of linked issues
- **AND** `deliveredCount` equals the number of linked issues whose current state represents delivered work

#### Scenario: Next issue recommendation

- **WHEN** an Epic has linked issues
- **THEN** `nextIssue` is the first blocked issue if any exists
- **AND** otherwise the first active issue if any exists
- **AND** otherwise the first backlog issue if any exists
- **AND** otherwise the response indicates the Epic is ready to mark done

#### Scenario: Empty Epic progress

- **WHEN** an Epic has no linked issues
- **THEN** progress reports zero delivered and zero total
- **AND** no issue workflow data is created or changed

### Requirement: Epic Lifecycle

System SHALL let users explicitly mark an Epic done or close it without automatically completing it from issue progress.

#### Scenario: Mark Epic done

- **WHEN** a user marks an Epic done
- **THEN** only the Epic status changes to `done`
- **AND** linked issues are not modified

#### Scenario: Close Epic

- **WHEN** a user closes an Epic
- **THEN** only the Epic status changes to `closed`
- **AND** linked issues are not modified

#### Scenario: No automatic completion

- **WHEN** all linked issues are delivered
- **THEN** the system does not automatically mark the Epic done
- **AND** the projected next state can indicate ready to mark done

