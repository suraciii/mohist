## ADDED Requirements

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

## REMOVED Requirements

### Requirement: Background reconciliation skips non-in-progress issues

**Reason:** The daily background reconciliation sweep (`IssueWorkflowReconciliationService`) and its hosted registration are deleted. Issue→`Done` transition is now driven by the event subscription on `com.mohist.workflow.run.completed` (see `issue-workflow-completion`), so a periodic sweep requirement is no longer applicable and would contradict the removal of the sweep. Keeping it would also re-introduce the needless complexity (24h latency, dual paths) this change removes.

**Migration:** After this change, the sweep and lazy read-path reconciliation no longer provide an automatic fallback. During the documented transition period (until a durable at-least-once event mechanism — transactional outbox + dispatcher + DLQ, or event-store replay — lands in a separate issue), a momentary event-dispatch or handler failure can leave an issue stuck in `InProgress` with no automatic recovery; such issues MUST be manually re-triggered (e.g. by re-emitting the completed event or re-running the workflow). There is no data migration: the deleted sweep owned no persistent state.
