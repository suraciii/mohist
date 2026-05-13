## MODIFIED Requirements

### Requirement: REQ-WFE-001 Checks are read-only validators

Workflow checks SHALL be read-only validators. A check SHALL return fact evidence through `CheckResult` and SHALL NOT write durable artifacts, modify code, mutate git state, schedule repair work, advance stages, request approval, or update WorkflowRun state directly.

#### Scenario: Check returns evidence only

- **WHEN** a check runs
- **THEN** it SHALL return status, message, and optional output evidence
- **AND** it SHALL NOT perform code-changing, file-changing, git-changing, approval-changing, or stage-changing side effects

#### Scenario: Repair is modeled as a task

- **WHEN** a failed check is repairable by policy
- **THEN** WorkflowRun or StageRun SHALL schedule a task with causedBy metadata
- **AND** the check implementation SHALL NOT run the repair itself

### Requirement: REQ-WFE-002 Failed checks run explicit fix tasks by policy

Failed checks SHALL be handled by WorkflowRun/StageRun policy decisions. If a policy maps the failed check to a fix task, the aggregate SHALL schedule that task, require the runner to report its task result, and only then allow the relevant check to run again.

#### Scenario: Health check fix is visible

- **WHEN** `health:build` fails and has a `fix-build-health` policy
- **THEN** WorkflowRun SHALL append a `fix-build-health` task to the current StageRun
- **AND** it SHALL re-run `health:build` only after the fix task completes

#### Scenario: Max attempts stops current stage

- **WHEN** a failed check still fails after its configured fix attempts
- **THEN** WorkflowRun SHALL keep the failed check results and fix task results
- **AND** the current stage SHALL fail with a traceable check failure reason
- **AND** the workflow SHALL NOT escalate to another stage through a fallback chain

### Requirement: REQ-WFE-WORKFLOW-RUN-001 Workflow engine updates WorkflowRun runtime state

The workflow engine SHALL execute work requested by the active WorkflowRun aggregate and SHALL NOT decide next stage, stage pass/fail, awaiting approval, or workflow completion from runner-local `StageRunResult.nextStage` data. Stage lifecycle, task results, check results, approval snapshots, and terminal workflow status SHALL be decided by WorkflowRun and persisted transactionally before projection updates.

#### Scenario: Stage lifecycle is aggregate-decided

- **WHEN** a task or check result is reported
- **THEN** WorkflowRun SHALL decide whether the current StageRun remains running, awaits approval, completes, or fails
- **AND** the WorkflowEngine SHALL NOT directly mark the stage passed or failed

#### Scenario: Next stage comes from stage order

- **WHEN** the current StageRun completes
- **THEN** WorkflowRun SHALL derive the next stage from its stage order
- **AND** WorkflowEngine SHALL NOT use `StageRunResult.nextStage` to update issue stage

#### Scenario: Results update WorkflowRun before projections

- **WHEN** a runner records a task result, check result, or approval response
- **THEN** the matching WorkflowRun task, check, or approval snapshot SHALL be updated first
- **AND** `stage_executions`, `stage_states`, issue stage/status, and check suites MAY then be updated as projections or audit evidence
