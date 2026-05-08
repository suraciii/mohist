## MODIFIED Requirements

### Requirement: REQ-WEB-001 Done worktrees are presented as retained inspection context

The Web UI SHALL show retained Done worktrees as review, traceability, diff inspection, and debugging context. It SHALL make clear that archive removes the retained worktree.

#### Scenario: Done issue worktree copy explains retention
- **GIVEN** a Done issue has a worktree
- **WHEN** the user views the issue detail
- **THEN** the UI SHALL explain the worktree is retained for review or traceability
- **AND** the UI SHALL indicate archiving removes the retained worktree

### Requirement: REQ-WEB-002 Web archive actions display backend feedback

Web archive actions SHALL surface backend warning and error feedback instead of silently invalidating data.

#### Scenario: Single archive displays warning
- **GIVEN** the archive API returns a warning
- **WHEN** the user archives one issue in the Web UI
- **THEN** the warning SHALL be visible to the user

#### Scenario: Archive error is visible
- **GIVEN** the archive API returns an error
- **WHEN** the user archives an issue in the Web UI
- **THEN** the error message SHALL be visible to the user

#### Scenario: Batch archive displays skipped summary
- **GIVEN** batch archive returns `skipped` and `skippedNumbers`
- **WHEN** the user runs batch archive from the Web UI
- **THEN** the UI SHALL display archived count, skipped count, skipped issue numbers, and the explanatory message

### Requirement: REQ-WEB-003 Done column exposes first archive action

The Done column SHALL expose batch archive whenever there are visible Done issues, even when no issues have previously been archived.

#### Scenario: Done column has no archived issues yet
- **GIVEN** the Done column contains at least one issue
- **AND** `archivedCount` is zero
- **WHEN** the user views the board
- **THEN** the batch archive action SHALL be visible

### Requirement: REQ-WEB-004 Archived page is history-only

The Archived page SHALL support viewing and searching archived issue history and navigating to issue detail. It SHALL NOT expose restore, unarchive, worktree restore, checkpoint restore, or re-execution actions.

#### Scenario: Archived page omits restore actions
- **GIVEN** archived issues exist
- **WHEN** the user views the Archived page
- **THEN** archived issues SHALL be listed and searchable
- **AND** each archived issue SHALL link to issue detail
- **AND** no restore or unarchive action SHALL be shown
