## ADDED Requirements

### Requirement: Append-only workflow log
The system SHALL maintain an append-only `workflow_log` table recording all workflow events. Each record SHALL include: issue_id, timestamp, event_type, stage, and data (JSON).

#### Scenario: Auto-record stage events
- **WHEN** the Main Agent enters or exits a stage
- **THEN** a stage_enter or stage_exit event SHALL be appended to workflow_log

#### Scenario: Auto-record agent events
- **WHEN** a sub-agent is spawned or completes
- **THEN** an agent_spawn or agent_done event SHALL be appended to workflow_log

#### Scenario: Auto-record user events
- **WHEN** the user performs an action (approve, rollback, comment)
- **THEN** a human_action event SHALL be appended to workflow_log

### Requirement: Progress from workflow log
The `mo status` command SHALL derive progress information from the workflow_log table, not from mutable state fields.

#### Scenario: Status timeline
- **WHEN** the user runs `mo status 42`
- **THEN** the system SHALL query workflow_log for issue #42
- **THEN** it SHALL display a timeline of events ordered by timestamp

#### Scenario: Current stage from log
- **WHEN** the user runs `mo status 42`
- **THEN** the current stage SHALL be determined from the most recent stage_enter event
