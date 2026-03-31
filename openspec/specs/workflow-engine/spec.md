## Requirements

### Requirement: Stage enum contains only M1 stages
The `Stage` enum SHALL contain only values used by M1: `Draft`, `Designing`, `Implementing`, `Done`. The values `WaitingDesignReview` and `WaitingReview` SHALL be removed.

#### Scenario: Stage enum values
- **WHEN** the Stage enum is inspected
- **THEN** it SHALL contain exactly 4 values: `draft`, `designing`, `implementing`, `done`
- **AND** it SHALL NOT contain `waiting-design-review` or `waiting-review`

### Requirement: Task infrastructure is removed
The `Task` interface SHALL be removed from `types/index.ts`. The `TaskRepo` class SHALL be deleted. The `tasks` SQLite table SHALL be dropped.

#### Scenario: No Task type
- **WHEN** the types module is inspected
- **THEN** it SHALL NOT export a `Task` interface

#### Scenario: No TaskRepo
- **WHEN** the db module is inspected
- **THEN** it SHALL NOT export `TaskRepo`

#### Scenario: Tasks table dropped
- **WHEN** the server starts and initializes the database
- **THEN** the `tasks` table SHALL NOT exist
