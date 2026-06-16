## ADDED Requirements

### Requirement: Runner restore on update failure after runner stopped
When `mo update` stops the runner service for the server update and the update subsequently fails, times out, or is interrupted, the CLI SHALL attempt to restore the runner service before exiting.

#### Scenario: Runner restore after update command failure
- **WHEN** `mo update` stops the runner
- **AND** the server build or restart command fails
- **THEN** the CLI SHALL attempt to start the runner service
- **AND** SHALL print a recovery message indicating runner restoration was attempted
- **AND** the exit code SHALL reflect the original update failure, not the recovery result

#### Scenario: Runner restore after server readiness timeout
- **WHEN** `mo update` stops the runner
- **AND** server readiness checks do not pass within the timeout
- **THEN** the CLI SHALL attempt to start the runner service
- **AND** SHALL print a message reporting the runner status after recovery attempt
- **AND** the exit code SHALL reflect the readiness failure

#### Scenario: Runner restore succeeds after failure
- **WHEN** runner restoration succeeds after an update failure
- **THEN** the final output SHALL state that workflows are available with a recovery warning
- **AND** SHALL NOT print that Mohist is fully ready

#### Scenario: Runner restore fails after failure
- **WHEN** runner restoration fails
- **THEN** the final output SHALL state that workflows are unavailable
- **AND** SHALL print a direct next action: "Start the runner manually with: mo server start --runner"

### Requirement: User interruption triggers recovery path
When the user interrupts `mo update` with SIGINT (Ctrl-C) or SIGTERM, the CLI SHALL enter the same recovery path used for update failures.

#### Scenario: Ctrl-C during update enters recovery
- **WHEN** the user presses Ctrl-C during `mo update`
- **AND** the runner was stopped for the server update
- **THEN** the CLI SHALL catch the interrupt signal
- **AND** SHALL attempt to restore the runner service
- **AND** SHALL print whether recovery succeeded or failed
- **AND** SHALL exit with a non-zero status code

#### Scenario: Ctrl-C after recovery prints final state
- **WHEN** recovery from Ctrl-C completes
- **THEN** the final output SHALL indicate the server and runner availability state
- **AND** SHALL provide an actionable next step if any capability is unavailable

#### Scenario: Ctrl-C before runner stop exits cleanly
- **WHEN** the user presses Ctrl-C before the CLI has stopped the runner
- **THEN** the CLI SHALL exit without attempting runner recovery
- **AND** SHALL print that no recovery was needed

### Requirement: Update reports final outcome by capability availability
The final output of `mo update` SHALL state a single outcome: ready, recovered with warnings, or failed with specific unavailable capabilities.

#### Scenario: Full success reports ready
- **WHEN** all update stages complete successfully
- **THEN** the final output SHALL state that Mohist is ready to run workflows
- **AND** the exit code SHALL be 0

#### Scenario: Partial success reports recovered with warnings
- **WHEN** the update completes but recovery was needed (e.g., runner restore after failure)
- **THEN** the final output SHALL state that Mohist is recovered with warnings
- **AND** SHALL list which capabilities were recovered

#### Scenario: Failure reports specific unavailable capability
- **WHEN** the update fails and recovery cannot restore all capabilities
- **THEN** the final output SHALL state which specific capability is unavailable (e.g., "Runner unavailable", "Server unavailable", "CLI unavailable")
- **AND** SHALL provide a direct next action to recover that capability
