## ADDED Requirements

### Requirement: Workflow scheduling state is authoritative and exclusive
The workflow engine SHALL maintain exactly one scheduling state per workflow within a backlog: waiting without an active lease, running with an active lease, or absent when the workflow is not runnable. A workflow MUST NOT be persisted in both `Waiting` and `Running` buckets for the same backlog.

#### Scenario: Registering a waiting workflow removes stale running state
- **WHEN** a runnable workflow is registered into a backlog
- **AND** the same workflow already exists in that backlog's `Running` bucket
- **THEN** the workflow engine SHALL remove the stale running claim before persisting the waiting entry
- **AND** the persisted backlog state SHALL NOT contain the workflow in both `Waiting` and `Running`

#### Scenario: Claiming a workflow removes waiting state
- **WHEN** a runner claims a workflow from a backlog
- **THEN** the workflow engine SHALL persist the workflow only in the `Running` bucket for that backlog
- **AND** the workflow SHALL NOT remain in the `Waiting` bucket for the same backlog

### Requirement: Startup recovery reconciles stale scheduling state
Workflow backlog recovery SHALL reconcile persisted backlog and workflow lease state against authoritative WorkflowRun state before making work available to runners. Recovery MUST remove backlog entries and active leases for workflows that are paused, failed, completed, cancelled, missing, or unable to provide runnable work.

#### Scenario: Recovery removes paused workflow scheduling state
- **WHEN** startup recovery inspects a persisted backlog entry or workflow lease
- **AND** the referenced WorkflowRun has status `paused`
- **THEN** recovery SHALL remove the workflow from all persisted waiting and running backlog buckets
- **AND** recovery SHALL clear any active lease for that workflow

#### Scenario: Recovery removes terminal workflow scheduling state
- **WHEN** startup recovery inspects a persisted backlog entry or workflow lease
- **AND** the referenced WorkflowRun has a terminal status such as `failed`, `completed`, or `cancelled`
- **THEN** recovery SHALL remove the workflow from persisted backlog state
- **AND** recovery SHALL clear any active lease for that workflow

#### Scenario: Recovery keeps only runnable workflows registered
- **WHEN** startup recovery completes for a project backlog
- **THEN** every persisted waiting workflow SHALL be runnable and have no active lease
- **AND** every persisted running workflow SHALL have an active lease
- **AND** no workflow SHALL appear in both waiting and running buckets for the same backlog

### Requirement: Terminal workflow transitions clean scheduling state
When workflow execution reaches a terminal workflow state, the workflow engine SHALL remove that workflow from persisted backlog state and clear active workflow lease ownership before or with the terminal transition becoming observable.

#### Scenario: Completed workflow releases scheduling state
- **WHEN** a WorkflowRun transitions to completed
- **THEN** the workflow engine SHALL remove the workflow from all waiting and running backlog buckets
- **AND** the workflow engine SHALL clear any active lease for that workflow

#### Scenario: Failed workflow releases scheduling state
- **WHEN** a WorkflowRun transitions to failed
- **THEN** the workflow engine SHALL remove the workflow from all waiting and running backlog buckets
- **AND** the workflow engine SHALL clear any active lease for that workflow
