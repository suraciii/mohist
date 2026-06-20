## MODIFIED Requirements

### Requirement: Epic Domain Model

System SHALL model an Epic as a named, described, prioritized long-running goal container with `active`, `paused`, `done`, and `closed` statuses. The `paused` status SHALL be a reversible, non-terminal lifecycle state that is distinct from any issue-health "blocked" concept; the system SHALL NOT reuse the word "blocked" for an Epic state.

#### Scenario: Create active Epic

- **WHEN** a user creates an Epic with title, description, and priority
- **THEN** the system persists the Epic with status `active`
- **AND** the Epic has timestamps suitable for list and detail display

#### Scenario: Epic is not executable work

- **WHEN** the system stores or reads Epics
- **THEN** Epics are separate from issues
- **AND** Epics do not have workflow stage, run state, worktree, branch, task execution, or check execution fields

#### Scenario: Paused is a reversible non-terminal state

- **WHEN** an Epic transitions into `paused`
- **THEN** the Epic is not considered terminal (`IsTerminal` SHALL remain true only for `done` and `closed`)
- **AND** the Epic MUST be able to return to `active` via Resume
- **AND** the Epic status enum and derived projection logic SHALL treat `paused` as a distinct branch alongside `active`, `done`, and `closed`

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

System SHALL let users explicitly pause, resume, mark done, or close an Epic without automatically completing it from issue progress. Status transitions SHALL obey the following locked rules: `active ↔ paused` via `Pause()` and `Resume()` SHALL be legal; `paused → closed` via `Close()` SHALL be legal; `paused → done` SHALL be forbidden. The `done` and `closed` statuses are terminal; `paused` and `active` are non-terminal. Entering `paused` SHALL NOT unbind any linked issue, SHALL NOT change issue workflow state, and SHALL NOT block Edit, link, unlink, or Close operations on the Epic. A terminal-state guard SHALL reject transitions out of `done` or `closed` but SHALL NOT fire on `paused`.

#### Scenario: Pause an active Epic

- **WHEN** a user pauses an `active` Epic, optionally supplying a pause reason
- **THEN** only the Epic status changes to `paused`
- **AND** the optional pause reason is persisted on the Epic
- **AND** linked issues are not modified or unbound
- **AND** the Epic is not considered terminal

#### Scenario: Resume a paused Epic

- **WHEN** a user resumes a `paused` Epic
- **THEN** only the Epic status changes back to `active`
- **AND** the persisted pause reason is cleared
- **AND** linked issues are not modified

#### Scenario: Close a paused Epic directly

- **WHEN** a user closes a `paused` Epic
- **THEN** the transition is allowed without first resuming
- **AND** only the Epic status changes to `closed`

#### Scenario: Paused Epic cannot be marked done directly

- **WHEN** a user attempts to mark done on a `paused` Epic
- **THEN** the system rejects the transition
- **AND** the error indicates the Epic MUST be resumed to `active` first

#### Scenario: Mark Epic done

- **WHEN** a user marks an `active` Epic done
- **THEN** only the Epic status changes to `done`
- **AND** linked issues are not modified

#### Scenario: Close Epic

- **WHEN** a user closes an `active` or `paused` Epic
- **THEN** only the Epic status changes to `closed`
- **AND** linked issues are not modified

#### Scenario: No automatic completion

- **WHEN** all linked issues are delivered
- **THEN** the system does not automatically mark the Epic done
- **AND** the projected next state can indicate ready to mark done

#### Scenario: Paused Epic remains editable

- **WHEN** an Epic is `paused`
- **THEN** a user can still Edit the Epic, link issues, unlink issues, and Close the Epic
- **AND** the terminal-state guard does not reject these operations on `paused`
