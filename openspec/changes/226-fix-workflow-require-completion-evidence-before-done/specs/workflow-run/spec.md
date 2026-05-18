## MODIFIED Requirements

### Requirement: WorkflowRun stage completion requires promised run evidence
WorkflowRun SHALL decide stage completion by comparing the active StageDefinition promise with the active StageRun evidence, and SHALL NOT treat an empty remaining work queue or vacuous task/check collection as successful completion.

#### Scenario: Missing static task evidence blocks completion
- **GIVEN** a stage definition declares a static task
- **WHEN** the matching StageRun has no corresponding successful terminal TaskRun
- **THEN** WorkflowRun SHALL report the stage as not complete with a recoverable missing-task-evidence reason
- **AND** `nextWork()` and explicit completion paths SHALL NOT advance the stage

#### Scenario: Missing static check evidence blocks completion
- **GIVEN** a stage definition declares a required check
- **WHEN** the matching StageRun has no corresponding current passed CheckRun
- **THEN** WorkflowRun SHALL report the stage as not complete with a recoverable missing-check-evidence reason
- **AND** `nextWork()` and explicit completion paths SHALL NOT advance the stage

#### Scenario: Existing run-owned work must finish successfully
- **GIVEN** a StageRun contains a dynamic, repair, rebase, retry, or convergence TaskRun
- **WHEN** that TaskRun is pending, running, failed, or skipped
- **THEN** WorkflowRun SHALL prevent later checks, approval, stage completion, and workflow completion
- **AND** the task SHALL remain required evidence for this run until it reaches a successful terminal state

#### Scenario: Required approval is explicit evidence
- **GIVEN** a stage requires user approval
- **WHEN** all required tasks and checks have passed but approval has not been approved
- **THEN** WorkflowRun SHALL return an approval wait instead of stage completion

### Requirement: Build dynamic work source evaluation is completion evidence
WorkflowRun SHALL require Build dynamic work source evaluation evidence before Build can complete. Generated Build task identities SHALL be materialized into the Build StageRun as run-owned TaskRun records and SHALL NOT be copied into static StageDefinition tasks.

#### Scenario: Unevaluated Build work source blocks completion
- **GIVEN** the Build stage uses a dynamic work source such as `tasks.json`
- **WHEN** the Build StageRun has no recorded source evaluation state
- **THEN** WorkflowRun SHALL block Build completion with a dynamic-source-not-evaluated reason

#### Scenario: Missing invalid or empty Build source blocks completion
- **WHEN** Build source evaluation records that `tasks.json` is missing, invalid, or contains zero tasks
- **THEN** WorkflowRun SHALL block Build completion with a clear recoverable source failure reason
- **AND** the issue SHALL NOT advance as if Build had no required work

#### Scenario: Materialized Build tasks become required run evidence
- **WHEN** Build source evaluation produces one or more tasks
- **THEN** the system SHALL append or preserve those tasks as Build StageRun TaskRun records
- **AND** every materialized Build TaskRun SHALL participate in the shared completion guard for that run

### Requirement: Check completion uses current review and merge evidence
Check completion SHALL depend on current StageRun task/check evidence for the candidate being approved, not raw AgentSession status or absence of work.

#### Scenario: Missing current review evidence blocks Check
- **WHEN** Check lacks a successful current AI review task or equivalent authoritative review result for the current candidate
- **THEN** WorkflowRun SHALL prevent Check completion and approval

#### Scenario: Missing review or merge checks block Check
- **WHEN** Check lacks current passed required review-verdict, verification, or merge-readiness CheckRun evidence
- **THEN** WorkflowRun SHALL prevent Check completion and approval

#### Scenario: Stale session status is not authoritative
- **GIVEN** an earlier AgentSession failed
- **WHEN** later current StageRun task/check evidence proves Check and Integrate success
- **THEN** WorkflowRun completion SHALL NOT be blocked solely by the stale failed session
- **AND** a later successful AgentSession SHALL NOT substitute for missing StageRun evidence

### Requirement: Integrate evidence is required for final workflow completion
WorkflowRun SHALL require the workflow to pass the final Integrate StageRun with required task, check, and delivery evidence before reporting workflow completion.

#### Scenario: Workflow cannot pass before final stage
- **WHEN** a WorkflowRun has not reached and passed the configured final Integrate stage
- **THEN** WorkflowRun SHALL NOT report the run as passed

#### Scenario: Missing Integrate delivery evidence blocks Done
- **WHEN** Integrate lacks successful required TaskRun evidence, passed required CheckRun evidence, or delivery facts such as spec sync, archive, merge, or final health evidence required by the Integrate model
- **THEN** WorkflowRun SHALL block final completion with a clear reason
- **AND** merge state alone SHALL NOT mark the workflow passed
