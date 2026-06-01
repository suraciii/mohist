## ADDED Requirements

### Requirement: Paused workflow runs are removed from scheduling state
WorkflowRun lifecycle transitions that make a run paused or otherwise non-runnable SHALL release the run from workflow scheduling state. The release MUST remove backlog waiting and running entries and clear active work lease ownership for the run.

#### Scenario: Pausing a workflow with an active lease clears ownership
- **WHEN** a WorkflowRun is paused while it has an active workflow lease
- **THEN** the workflow run lifecycle SHALL clear the active lease for that workflow
- **AND** it SHALL remove the workflow from persisted backlog running state
- **AND** future runner polls SHALL NOT claim that paused workflow as runnable work

#### Scenario: Cancelling an issue pauses and unschedules its workflow
- **WHEN** an issue is cancelled and its active WorkflowRun is paused because of cancellation
- **THEN** the workflow SHALL be absent from persisted waiting and running backlog buckets
- **AND** any active workflow lease for the cancelled issue's run SHALL be cleared or marked abandoned consistently

### Requirement: Non-runnable workflow runs do not expose work leases
WorkflowRun SHALL NOT retain active work ownership after the run becomes paused, failed, completed, or cancelled. If an in-flight work item cannot complete because the run became non-runnable, the run MUST preserve diagnostic evidence without leaving an active scheduling lease.

#### Scenario: In-flight work is abandoned during cancellation
- **WHEN** a workflow has in-flight leased work
- **AND** the owning issue is cancelled
- **THEN** the workflow run SHALL stop exposing that work as actively leased
- **AND** it SHALL record cancellation or abandonment evidence for diagnostics
- **AND** the workflow SHALL NOT remain claimable as running backlog work

#### Scenario: Terminal run has no lease
- **WHEN** a WorkflowRun status is failed, completed, or cancelled
- **THEN** no active workflow lease SHALL remain for that run
- **AND** the run SHALL be absent from runnable backlog state
