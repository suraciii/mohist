## ADDED Requirements

### Requirement: Workflow execution records work item attempts

The workflow engine SHALL record task and check execution through WorkflowRun work item attempt transitions. Execution SHALL start a running attempt before dispatching a work item and SHALL complete, fail, or interrupt that attempt according to the actual execution outcome.

#### Scenario: Build task execution records attempts

- **WHEN** Build dispatches a task work item
- **THEN** the matching task SHALL start a `running` latest attempt before work is dispatched
- **AND** the attempt SHALL become `completed` or `failed` when the task result is known

#### Scenario: Check execution records attempts

- **WHEN** a stage dispatches a check work item
- **THEN** the matching check SHALL start a `running` latest attempt before the check is run
- **AND** the attempt SHALL become `completed` or `failed` when the check result is known

#### Scenario: Genuine execution failure remains failed

- **WHEN** a task or check handler returns a genuine failed result or error result
- **THEN** the latest work item attempt SHALL become `failed`
- **AND** retry eligibility MAY be derived from that failed latest attempt

### Requirement: Stopped or lost execution interrupts attempts

The workflow engine SHALL distinguish stopped or lost execution from failed work results. Intentional stop, cancelled session state, lost process, or stale running evidence SHALL mark the related running work item attempt interrupted unless a genuine failed work result exists.

#### Scenario: Intentional agent stop interrupts current work

- **WHEN** Mohist intentionally stops an agent that is executing a workflow work item
- **THEN** the related coder session SHALL be marked cancelled or interrupted
- **AND** the current work item's latest attempt SHALL become `interrupted` with diagnostic reason
- **AND** historical stop evidence SHALL remain visible for inspection

#### Scenario: Lost execution does not become failed

- **WHEN** a running agent process or session disappears without a failed task or check result
- **THEN** the latest attempt SHALL become `interrupted`
- **AND** the system SHALL NOT expose the work as retryable failed work solely because execution was lost

### Requirement: Reconcile stale running attempts before recovery decisions

The workflow engine SHALL reconcile the current work item's latest `running` attempt against live execution evidence before recovery-sensitive reads, writes, and workflow resume decisions.

#### Scenario: Live evidence keeps attempt running

- **WHEN** the latest attempt is `running`
- **AND** an active queue task, live related coder session, or live related agent process proves execution is still active
- **THEN** reconciliation SHALL leave the attempt `running`
- **AND** recovery guidance SHALL be wait or stop

#### Scenario: Missing evidence interrupts attempt

- **WHEN** the latest attempt is `running`
- **AND** there is no running or pending queue task and no live related session or process evidence
- **THEN** reconciliation SHALL idempotently mark the attempt `interrupted`
- **AND** it SHALL record an interruption reason such as `agent-stopped` or `agent-lost`
- **AND** the workflow summary SHALL move to waiting for recovery

#### Scenario: Reconciliation is invoked on recovery paths

- **WHEN** issue detail, stage-state, queue recovery, retry availability, resume, rerun, CLI status, or workflow resume code evaluates primary recovery actions
- **THEN** it SHALL use the reconciled latest attempt state before exposing or accepting those actions
