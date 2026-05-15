## ADDED Requirements

### Requirement: Config-driven runner executes declared stage work

The workflow engine SHALL provide a config-driven stage runner path that executes tasks, checks, approvals, repairs, and invalidations from stage definitions and registries. The runner SHALL orchestrate work requested by WorkflowRun and SHALL NOT duplicate WorkflowRun stage progression decisions in stage-specific subclasses.

#### Scenario: Runner executes requested task from registries

- **WHEN** `WorkflowRun.nextWork()` returns a task for the current stage
- **THEN** the config-driven runner SHALL resolve the executable task from the stage definition work sources and task loader registry
- **AND** it SHALL execute the task through the handler selected by the task execution policy
- **AND** it SHALL report the task result back to WorkflowRun before selecting later work

#### Scenario: Runner executes requested check from registry

- **WHEN** `WorkflowRun.nextWork()` returns a check for the current stage
- **THEN** the config-driven runner SHALL resolve the check from the stage definition check policy and check registry
- **AND** it SHALL report the check result back to WorkflowRun before selecting later work

#### Scenario: Runner does not decide stage progression

- **WHEN** a task, check, repair task, or approval result is reported
- **THEN** WorkflowRun SHALL decide whether the stage continues, awaits approval, passes, or fails
- **AND** the config-driven runner SHALL NOT derive the next stage from runner-local stage result data

### Requirement: Legacy and config-driven runner paths coexist during migration

The stage execution infrastructure SHALL keep the legacy runner path available while each stage is migrated to config-driven execution. The system SHALL NOT delete legacy Plan, Build, Check, or Integrate runner files or remove the rollback path in this change.

#### Scenario: Unmigrated stage can use legacy runner path

- **WHEN** a stage has not yet been enabled for config-driven execution
- **THEN** the workflow engine SHALL be able to execute that stage through the existing legacy runner path
- **AND** existing task, check, repair, approval, checkpoint, event, and log behavior SHALL remain available

#### Scenario: Migrated stage uses config-driven path independently

- **WHEN** one stage is enabled for config-driven execution and another stage is still legacy
- **THEN** the enabled stage SHALL execute through the config-driven path
- **AND** the legacy stage SHALL remain executable through the legacy path

#### Scenario: Unified runner becomes default only after all stages migrate

- **WHEN** Integrate, Plan, Check, and Build have each passed their config-driven validation
- **THEN** WorkflowEngine SHALL use the unified config-driven stage runner as the default runner for those stages
- **AND** the legacy runner files SHALL remain present as rollback implementation during this issue

### Requirement: Config-driven checks preserve read-only and repair policy boundaries

The config-driven runner SHALL execute checks as read-only validators and SHALL schedule repair tasks only through WorkflowRun or StageRun repair policy decisions.

#### Scenario: Failed check schedules configured repair task

- **WHEN** a config-driven check fails and the stage repair policy allows a repair task for that check
- **THEN** WorkflowRun or StageRun SHALL append the configured repair task with causedBy metadata
- **AND** the check implementation SHALL NOT run the repair itself
- **AND** the runner SHALL execute the repair task as ordinary task work before the relevant check is re-evaluated

#### Scenario: Approval remains a user decision point

- **WHEN** ordinary non-approval checks have passed and the stage requires approval
- **THEN** WorkflowRun SHALL expose an approval wait state rather than treating approval as a repairable check failure
- **AND** the runner SHALL preserve existing approval output semantics for Plan and Check

### Requirement: Config-driven invalidation applies branch and repair facts

The config-driven workflow path SHALL apply invalidation policies from stage definitions after task results report facts that make prior task, check, or approval state stale.

#### Scenario: Review repair invalidates stale review state

- **WHEN** a Check-stage repair task changes code or review-relevant artifacts
- **THEN** the configured invalidation policy SHALL reset stale AI review, review-passed, merge-ready, and approval state before approval can be requested again

#### Scenario: Rebase facts drive invalidation

- **WHEN** `rebase-branch` completes and reports branch facts
- **THEN** the configured invalidation policy SHALL decide whether dependent tasks, checks, or approval state are invalidated based on those facts
- **AND** a rebase that does not change the candidate snapshot SHALL NOT force re-review solely because the rebase task ran

### Requirement: Aggregate single-work execution remains supported

The workflow engine SHALL preserve aggregate single task and single check execution behavior when using the config-driven runner.

#### Scenario: Aggregate requested task executes once

- **WHEN** aggregate workflow execution invokes a runner for one requested task
- **THEN** the config-driven runner SHALL execute only that task
- **AND** it SHALL report exactly that task result before WorkflowRun computes the next work item

#### Scenario: Aggregate requested check executes once

- **WHEN** aggregate workflow execution invokes a runner for one requested check
- **THEN** the config-driven runner SHALL execute only that check
- **AND** it SHALL report exactly that check result before WorkflowRun computes the next work item
