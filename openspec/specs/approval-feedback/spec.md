# OpenSpec Capability: approval-feedback

### Requirement: ApprovalFeedback is a first-class domain entity

Mohist SHALL model approval feedback as a first-class domain entity scoped to a workflow run, stage, and approval gate. An `ApprovalFeedback` record SHALL be created when a user requests changes at an approval gate, and SHALL be resolved when the feedback has been applied. Feedback SHALL NOT be stored only as generic comments.

#### Scenario: Feedback is scoped to workflow run and stage

- **WHEN** a user requests changes at the Plan stage approval gate
- **THEN** the system SHALL create an `ApprovalFeedback` record with `workflowRunId`, `stage` set to `plan`, and the user's feedback body
- **AND** the feedback record SHALL have a unique stable id and `status = open`
- **AND** the feedback SHALL be queryable by issue number and stage

#### Scenario: Feedback lifecycle is open then resolved

- **WHEN** an open `ApprovalFeedback` exists
- **AND** the apply-feedback task completes successfully with a resolution summary
- **THEN** the feedback status SHALL transition to `resolved`
- **AND** the resolution summary SHALL be persisted
- **AND** `resolvedAt` SHALL be recorded

#### Scenario: Feedback is not a comment

- **WHEN** a user requests changes
- **THEN** the feedback body SHALL be stored in the `ApprovalFeedback` record
- **AND** the feedback SHALL NOT rely on generic issue comments as the only storage model
- **AND** the feedback SHALL appear in workflow approval history, not only in the comment thread

### Requirement: Requesting changes resumes the stage as running work

When a user requests changes at an approval gate, the stage SHALL leave the `AwaitingApproval` state and resume as running work. Requesting changes SHALL NOT mark the workflow or stage as failed.

#### Scenario: Stage resumes after feedback request

- **GIVEN** a stage is in `AwaitingApproval` state
- **WHEN** the user requests changes with feedback
- **THEN** the stage SHALL resume as running
- **AND** the workflow SHALL NOT be marked as failed
- **AND** the `apply-feedback` task SHALL be scheduled as the next work item

#### Scenario: Feedback does not cause stage failure

- **WHEN** a user requests changes
- **THEN** the WorkflowRun failure evidence SHALL NOT reference `approval-rejected`
- **AND** the stage failure reason SHALL NOT be set to approval rejection
- **AND** the workflow SHALL continue to accept further execution

### Requirement: Approval history links feedback and resolution

The user-facing approval timeline SHALL link the approval request, requested changes (feedback), feedback task execution, resolution, re-run checks, and the next approval request as a visible trail.

#### Scenario: Approval history shows feedback cycle

- **GIVEN** approval was requested for a stage
- **AND** the user requested changes
- **AND** an agent applied the feedback and wrote a resolution
- **AND** checks were rerun
- **AND** approval was requested again
- **THEN** the approval history SHALL display: initial approval request, feedback requested, feedback task, resolution summary, check results, and next approval request
- **AND** the history SHALL distinguish feedback cycles from separate stage attempts

#### Scenario: Feedback resolution is visible after the cycle

- **WHEN** feedback has been resolved and the stage has re-entered approval
- **THEN** the approval history SHALL show the resolution summary
- **AND** the prior feedback body SHALL remain accessible for inspection

### Requirement: Resolution summary is a concise agent-written record

When the apply-feedback task completes, the agent SHALL write a concise resolution summary. The resolution summary SHALL be stored on the `ApprovalFeedback` record.

#### Scenario: Agent writes resolution summary

- **WHEN** the apply-feedback agent task completes successfully
- **THEN** the task output SHALL include a resolution summary
- **AND** the resolution summary SHALL be persisted to the `ApprovalFeedback` record
- **AND** the summary SHALL be visible in approval history

#### Scenario: Open feedback has no resolution

- **WHEN** an `ApprovalFeedback` record is created
- **THEN** `resolutionSummary` SHALL be null
- **AND** `resolvedAt` SHALL be null

### Requirement: ApprovalFeedback data model is minimal and stable

The `ApprovalFeedback` runtime model SHALL include only fields with concrete product behavior. The model SHALL NOT include speculative fields such as category, severity, or source until they drive real behavior.

#### Scenario: Runtime model has required fields only

- **WHEN** an `ApprovalFeedback` record is inspected
- **THEN** it SHALL contain `Id`, `WorkflowRunId`, `Stage`, `Body`, `Status`, `CreatedAt`, `ResolutionTaskId`, `ResolvedAt`, and `ResolutionSummary`
- **AND** it SHALL NOT contain category, severity, source, or other speculative taxonomy fields
