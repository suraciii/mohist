### Requirement: Issue entity persists completion time on entering a terminal state

The issue entity SHALL persist a **completion time** field, symmetric with `createdAt` and `archivedAt`, recording the moment an issue enters a terminal state. A terminal state is `done` (reached when the issue's workflow completes) or `cancelled` (reached when the issue is closed). The completion time SHALL be written from the terminal-transition event time — the `IssueWorkCompleted` event for `done`, the `IssueClosed` event for `cancelled`. An issue that has never reached a terminal state SHALL have a null completion time.

#### Scenario: Completing an in-progress issue writes the completion time

- **WHEN** an issue in `in_progress` status transitions to `done`
- **THEN** the issue's completion time SHALL be set to the moment of the `IssueWorkCompleted` event
- **AND** the completion time SHALL be persisted on the issue entity

#### Scenario: Closing an issue writes the completion time

- **WHEN** an issue transitions to `cancelled` (closed)
- **THEN** the issue's completion time SHALL be set to the moment of the `IssueClosed` event
- **AND** the completion time SHALL be persisted on the issue entity

#### Scenario: Non-terminal issue has a null completion time

- **WHEN** an issue is in a non-terminal status (`backlog` or `in_progress`)
- **THEN** the issue's completion time SHALL be null

### Requirement: Completion time survives reopen and updates to the latest terminal moment

Reopening an issue SHALL NOT clear the persisted completion time; the field SHALL retain the previous terminal moment until the issue reaches a terminal state again. When an issue re-enters a terminal state after having been reopened, the completion time SHALL be overwritten to the latest terminal-transition event time, so it always reflects the most recent completion rather than the first.

#### Scenario: Reopen preserves the prior completion time

- **WHEN** a `cancelled` issue is reopened to `backlog`
- **THEN** the issue's completion time SHALL remain set to the prior terminal moment
- **AND** the completion time SHALL NOT be cleared

#### Scenario: Re-completing after reopen overwrites the completion time

- **WHEN** an issue that was previously terminal is reopened, then started, then transitions to `done`
- **THEN** the issue's completion time SHALL be overwritten to the moment of the new `IssueWorkCompleted` event
- **AND** the completion time SHALL reflect the most recent terminal transition, not the first

### Requirement: One-time backfill derives completion time for already-terminal issues from their completion event

Issues that are already in a terminal state when the completion-time field is introduced SHALL have their completion time backfilled in a one-time migration, derived from the issue's completion event corresponding to its current terminal state. The backfilled value SHALL match what the live write would have produced for that transition. The backfill SHALL be idempotent: re-running it SHALL NOT change an already-correct completion time.

#### Scenario: Backfill populates completion time for a done issue

- **WHEN** a one-time backfill runs for an issue that is already `done`
- **THEN** the issue's completion time SHALL be set from its `IssueWorkCompleted` event time
- **AND** the value SHALL match the moment the issue entered its current terminal state

#### Scenario: Backfill populates completion time for a cancelled issue

- **WHEN** a one-time backfill runs for an issue that is already `cancelled`
- **THEN** the issue's completion time SHALL be set from its `IssueClosed` event time

#### Scenario: Backfill is idempotent

- **WHEN** the one-time backfill runs a second time over an issue whose completion time was already backfilled
- **THEN** the completion time SHALL NOT change
