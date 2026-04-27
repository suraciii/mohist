## MODIFIED Requirements

### Requirement: Stage timeout SHALL be passed to ACP session runner
The workflow stage timeout defined in workflow configuration SHALL be passed to the ACP session runner as a per-task timeout, calculated by dividing the stage timeout by the number of remaining tasks with a minimum floor of 10 minutes. The default build stage timeout SHALL be 3600 seconds (60 minutes).

#### Scenario: Stage timeout with 6 tasks
- **WHEN** a build stage has timeout=3600 seconds and 6 tasks
- **THEN** each task's ACP session SHALL have a timeout of 600 seconds (10 minutes), exactly equal to the 10-minute floor

#### Scenario: Stage timeout with 10 tasks
- **WHEN** a build stage has timeout=3600 seconds and 10 tasks
- **THEN** each task's ACP session SHALL have a timeout of 360 seconds (6 minutes), but not less than the 10-minute floor, so 600 seconds applies

#### Scenario: Stage timeout with 2 tasks
- **WHEN** a build stage has timeout=3600 seconds and 2 tasks
- **THEN** each task's ACP session SHALL have a timeout of 1800 seconds (30 minutes)

#### Scenario: No stage timeout defined
- **WHEN** the workflow stage has no timeout configured
- **THEN** the ACP session SHALL use the default 30-minute timeout
