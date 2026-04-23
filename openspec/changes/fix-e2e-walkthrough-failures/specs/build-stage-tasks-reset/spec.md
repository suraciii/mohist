## ADDED Requirements

### Requirement: Tasks passes reset
The system SHALL detect and reset task `passes` fields that have been corrupted (all set to `true`) at the entry of `runRalphLoop`, before any task execution begins.

#### Scenario: Build stage starts with all passes=true (corrupted state)
- **WHEN** `runRalphLoop` is called with a tasks.json where all tasks have `passes: true`
- **THEN** the system SHALL set all tasks' `passes` to `false` before writing the updated tasks.json and beginning execution

#### Scenario: Build stage starts with mixed passes values (normal state)
- **WHEN** `runRalphLoop` is called with a tasks.json where some tasks have `passes: true` and others `passes: false`
- **THEN** the system SHALL NOT reset any tasks' `passes` values
- **AND** execution SHALL proceed with the existing passes state

### Requirement: Reset is persisted
The system SHALL write the reset tasks back to tasks.json before executing any task.

#### Scenario: Reset writes to disk
- **WHEN** the passes reset is performed
- **THEN** the system SHALL call `writeTasksFile` with the updated tasks array before the task execution loop begins
