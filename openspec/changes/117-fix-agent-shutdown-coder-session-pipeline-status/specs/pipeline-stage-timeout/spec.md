## ADDED Requirements

### Requirement: Pipeline stage timeout guard

The `executePipeline()` method in `AgentRunnerService` SHALL wrap the `pipeline.run()` call in an outer timeout independent of any underlying SDK-level timeout. If the pipeline does not complete within the timeout, the pipeline SHALL be aborted and the issue SHALL be marked as failed/blocked.

The default stage timeout SHALL be 30 minutes.

#### Scenario: Stage completes within timeout

- **WHEN** a pipeline stage (e.g. check stage AI Review) completes before the 30-minute timeout
- **THEN** the stage result is returned normally
- **AND** no timeout is triggered

#### Scenario: Stage exceeds timeout

- **WHEN** a pipeline stage does not complete within 30 minutes
- **THEN** the stage SHALL return `{ success: false, message: "Check stage timed out after 30 minutes" }`
- **AND** the issue SHALL be marked as failed/blocked
- **AND** the underlying SDK call (e.g. ACI `connection.prompt()`) SHALL be aborted via the stage's AbortController

#### Scenario: Stage timeout fires even if SDK internal timeout fails

- **WHEN** an ACI SDK `connection.prompt()` Promise neither resolves nor rejects (hangs indefinitely)
- **AND** the ACI internal timeout does not fire
- **THEN** the outer 30-minute stage timeout SHALL still fire
- **AND** the stage SHALL return a timeout failure result
- **AND** the issue SHALL not remain in an indefinite active state

#### Scenario: Stage timeout is configurable

- **WHEN** a custom stage timeout value is provided via configuration
- **THEN** the system SHALL use the custom timeout instead of the default 30 minutes
