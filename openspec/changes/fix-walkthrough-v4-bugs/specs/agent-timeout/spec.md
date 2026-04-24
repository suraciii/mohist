## ADDED Requirements

### Requirement: Stage timeout SHALL be passed to ACP session runner
The workflow stage timeout defined in workflow configuration SHALL be passed to the ACP session runner as a per-task timeout, calculated by dividing the stage timeout by the number of remaining tasks with a minimum floor of 5 minutes.

#### Scenario: Stage timeout with 10 tasks
- **WHEN** a build stage has timeout=1800 seconds and 10 tasks
- **THEN** each task's ACP session SHALL have a timeout of at least 180 seconds (3 minutes), but not less than the 5-minute floor

#### Scenario: Stage timeout with 2 tasks
- **WHEN** a build stage has timeout=1800 seconds and 2 tasks
- **THEN** each task's ACP session SHALL have a timeout of 900 seconds (15 minutes)

#### Scenario: No stage timeout defined
- **WHEN** the workflow stage has no timeout configured
- **THEN** the ACP session SHALL use the default 30-minute timeout
