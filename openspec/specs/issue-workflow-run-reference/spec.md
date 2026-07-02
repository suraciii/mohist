### Requirement: Workflow run reference is a persistent execution fact

An issue's workflow run reference (`workflowRunId`) records a workflow run that was once bound to the issue. It is an execution fact, not an indicator of whether the workflow is currently active or controllable. The reference SHALL survive issue transitions to `Done`, `Archive`, and `Cancel`/`Close` without being cleared, nulled, or reset. Whether a workflow is active/running/controllable SHALL be a derived judgment from the issue's status combined with the workflow run's state, never from the mere presence of a `workflowRunId`.

#### Scenario: Completing an issue preserves the workflow run reference

- **WHEN** an `InProgress` issue bound to workflow run `wr_1` transitions to `Done`
- **THEN** the issue's `workflowRunId` SHALL remain `wr_1`
- **AND** the issue's status SHALL be `Done`

#### Scenario: Archive preserves the workflow run reference

- **WHEN** a `Done` issue bound to workflow run `wr_1` is archived
- **THEN** the issue's `workflowRunId` SHALL remain `wr_1`
- **AND** `archivedAt` SHALL be set
- **AND** no other issue field referencing execution history SHALL be cleared

#### Scenario: Closing an issue preserves the workflow run reference

- **WHEN** an issue bound to workflow run `wr_1` is closed/cancelled
- **THEN** the issue's `workflowRunId` SHALL remain `wr_1`
- **AND** the issue's status SHALL be `Cancelled`

#### Scenario: Presence of a workflow run reference does not imply an active workflow

- **WHEN** a `Done` or archived issue has a non-null `workflowRunId`
- **THEN** control and reconciliation logic SHALL treat the issue as having no active workflow
- **AND** SHALL NOT attempt to start, stop, retry, or recover a workflow for it on the basis of the reference alone

### Requirement: Archive is a reversible visibility operation

Archiving an issue SHALL only set `archivedAt` and update `updatedAt`. Archiving SHALL NOT clear `workflowRunId`, delete events, artifacts, feedback, logs, commits, diffs, or any other execution-history data. Unarchiving SHALL only clear `archivedAt`; because archive destroys nothing, unarchive SHALL NOT be required to restore any previously-cleared data. Only `Done` issues MAY be archived.

#### Scenario: Archive sets only archivedAt and updatedAt

- **WHEN** a `Done` issue bound to workflow run `wr_1` is archived
- **THEN** `archivedAt` SHALL be set to a timestamp
- **AND** `updatedAt` SHALL advance to that timestamp
- **AND** `workflowRunId` SHALL remain `wr_1`

#### Scenario: Unarchive clears only archivedAt

- **WHEN** an archived issue is unarchived
- **THEN** `archivedAt` SHALL be cleared
- **AND** `workflowRunId` SHALL remain unchanged
- **AND** no execution-history data SHALL be restored or modified

#### Scenario: Archive rejects non-done issues

- **WHEN** an issue that is not `Done` is archived
- **THEN** the operation SHALL be rejected with a clear error

### Requirement: Workflow run reference naming is neutral

Internal domain fields, exceptions, logs, and judgments SHALL refer to the issue→workflow run link as a neutral "workflow run reference" (`workflowRunId`), not as an "active" run. The presence of the reference SHALL NOT carry active/running/controllable semantics in naming or in conditional logic. Active-workflow semantics SHALL be expressed as explicit status+run-state checks.

#### Scenario: No domain symbol names the reference as active

- **WHEN** the issue domain exposes or persists the workflow run link
- **THEN** the field/property name SHALL be `workflowRunId` (or `WorkflowRunId`)
- **AND** no field, exception message, or log line SHALL name it `activeWorkflowRunId`

#### Scenario: Active-workflow checks use explicit state

- **WHEN** code decides whether an issue has an active, controllable workflow
- **THEN** the decision SHALL combine the issue's status and the workflow run's state
- **AND** SHALL NOT be expressed as `workflowRunId != null`

### Requirement: Workflow status read path is a pure query

`IssueGrain.GetWorkflowStatusAsync` SHALL be a pure query: it SHALL return the bound workflow run's projected status/state without mutating the issue's own status and without producing any write side-effect. The read path SHALL NOT perform lazy terminal-state reconciliation (e.g. `ReconcileWithWorkflowTerminalStateAsync`) or otherwise advance an issue toward `Done`. Issue terminal-state transitions SHALL be driven solely by command/event paths (see `issue-workflow-completion`), never by the read path, preserving command/query separation.

#### Scenario: Read path reports a completed run without transitioning the issue

- **WHEN** `GetWorkflowStatusAsync` is called for an `InProgress` issue whose bound workflow run has reached `Completed`
- **THEN** it SHALL return the workflow run's completed status
- **AND** SHALL NOT transition the issue to `Done`
- **AND** SHALL NOT invoke any reconciliation write

#### Scenario: Read path never mutates issue state

- **WHEN** `GetWorkflowStatusAsync` is called any number of times for an issue
- **THEN** no call SHALL mutate the issue's status or any persisted issue field
- **AND** no call SHALL perform terminal-state reconciliation as a side-effect of the read
