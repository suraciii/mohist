# OpenSpec Capability: workflow-run

## ADDED Requirements

### Requirement: WorkflowRun owns a runtime task output variable store

WorkflowRun SHALL own a runtime variable store keyed by `tasks.<taskDefinitionId>.outputs.<name>`. The store SHALL be part of the WorkflowRun aggregate state and SHALL be updated transactionally when successful task results are reported.

#### Scenario: WorkflowRun initializes with empty runtime store
- **WHEN** a WorkflowRun is created
- **THEN** the runtime variable store SHALL be empty
- **AND** it SHALL be included in the aggregate state

#### Scenario: Successful task result appends runtime variables
- **WHEN** a successful task result reports captured outputs
- **THEN** WorkflowRun SHALL append those outputs to the runtime store
- **AND** the update SHALL be persisted as part of the same aggregate transition

#### Scenario: Failed task result leaves store unchanged
- **WHEN** a failed task result is reported
- **THEN** WorkflowRun SHALL NOT modify the runtime store for that task
- **AND** any existing entries for the same task id SHALL remain unchanged

### Requirement: Runtime variables persist across stage boundaries

The WorkflowRun runtime variable store SHALL persist across stage transitions within the same workflow run. Variables captured in one stage SHALL remain available for resolution in subsequent stages without redeclaration or recapture.

#### Scenario: Variables captured in Plan are available in Build
- **WHEN** a Plan task captures a runtime output
- **AND** the WorkflowRun advances to Build
- **THEN** the captured variable SHALL remain in the runtime store
- **AND** Build task dispatches SHALL resolve the variable through the existing template chain

#### Scenario: Variables survive stage retry
- **WHEN** a stage is retried after a later stage failure
- **THEN** runtime variables captured before the retry point SHALL remain available
- **AND** variables captured within the retried stage SHALL be overwritten on successful recapture

### Requirement: Runtime variables use write-once semantics

A runtime variable key SHALL be written when its task first succeeds and SHALL NOT be mutated except by retrying the same task and producing a new successful result. External runtime injection and cross-workflow variable sharing SHALL NOT be supported.

#### Scenario: Same task output is overwritten on retry
- **WHEN** a task succeeds, fails on retry, and then succeeds again
- **THEN** the runtime variable for that task output SHALL reflect the latest successful capture
- **AND** failed retry attempts SHALL NOT modify the variable

#### Scenario: External sources cannot inject runtime variables
- **WHEN** a dispatch request attempts to inject variables under `tasks.*.outputs.*`
- **THEN** WorkflowRun SHALL reject or ignore the injection
- **AND** only successful task results SHALL populate that namespace
