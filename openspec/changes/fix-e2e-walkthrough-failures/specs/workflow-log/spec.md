## MODIFIED Requirements

### Requirement: CLI logs shows pipeline events
The `mo issue logs <number>` command SHALL retrieve and display pipeline-level events from the workflow_log table in addition to agent session files.

#### Scenario: User runs mo issue logs for a blocked issue
- **WHEN** the user runs `mo issue logs <number>` for an issue that has pipeline events in workflow_log
- **THEN** the output SHALL include build stage events (build_started, build_completed, build_failed) and task events (task_started, task_completed, task_failed)
- **AND** each event SHALL be displayed with its timestamp, event type, and a human-readable summary

#### Scenario: Both pipeline events and log files exist
- **WHEN** the user runs `mo issue logs <number>` and both pipeline events exist in workflow_log and agent log files exist on disk
- **THEN** the command SHALL display the pipeline events first (structured overview)
- **AND** the command SHALL also display the agent session log files (detailed output)

#### Scenario: No pipeline events exist
- **WHEN** the user runs `mo issue logs <number>` and no pipeline events exist in workflow_log
- **THEN** the command SHALL display the agent session log files as before
