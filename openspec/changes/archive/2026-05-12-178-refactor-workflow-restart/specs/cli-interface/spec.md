## MODIFIED Requirements

### Requirement: issue-recovery-verbs-are-user-intent-based

The CLI SHALL present issue recovery verbs according to user intent. `reopen` SHALL mean reopening a closed issue, `resume` SHALL mean continuing paused or interrupted work, and recovery help text SHALL not mention restart.

#### Scenario: Reopen command is closed-only

- **WHEN** the user runs `mo issue reopen <number>`
- **THEN** the CLI calls the reopen API
- **AND** the command semantics are described as reopening a closed issue

#### Scenario: Resume command targets paused or interrupted work

- **WHEN** the user runs `mo issue resume <number>`
- **THEN** the CLI calls the resume API
- **AND** success output describes the issue as resumed rather than reopened

#### Scenario: Failed recovery guidance omits restart

- **WHEN** a failed or needs-action recovery command returns an error
- **THEN** the CLI guidance references retry, rerun, or rewind as appropriate
- **AND** the CLI does not recommend restart

#### Scenario: Closed guidance uses reopen only

- **WHEN** a closed issue blocks further progress in CLI output
- **THEN** the guidance recommends `mo issue reopen <number>`
- **AND** it does not recommend resume, retry, or restart for that closed-only case
