## ADDED Requirements

### Requirement: Issue read models expose the completion time

The issue list, issue detail, and archived-issue detail read models SHALL expose the issue's `completedAt` field, sourced from the single persisted completion time on the issue entity. For an issue that has reached a terminal state the value SHALL be the terminal-transition moment; for an issue that has never been terminal the value SHALL be null. The archived-issue detail path SHALL expose `completedAt` exactly as the non-archived detail does, so the field is consistent across every read surface.

#### Scenario: List read model includes completion time for a terminal issue

- **WHEN** a client requests the issue list
- **AND** an issue in the result is in `done` status
- **THEN** that issue's entry SHALL include `completedAt` set to its terminal-transition moment

#### Scenario: Detail read model includes completion time for a cancelled issue

- **WHEN** a client requests `GET /api/issues/:number` for a `cancelled` issue
- **THEN** the response SHALL include `completedAt` set to the moment the issue was closed

#### Scenario: Non-terminal issue exposes a null completion time

- **WHEN** a client requests the detail or list of an issue in `in_progress` status
- **THEN** the response SHALL include `completedAt: null`

#### Scenario: Archived detail read model exposes completion time

- **WHEN** a client requests the detail of an archived `done` issue
- **THEN** the response SHALL include `completedAt` set to the issue's completion moment
- **AND** the value SHALL match the `completedAt` the non-archived detail exposed before archiving
