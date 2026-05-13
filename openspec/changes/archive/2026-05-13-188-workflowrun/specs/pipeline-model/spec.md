## MODIFIED Requirements

### Requirement: REQ-PM-WORKFLOW-RUN-001 Pipeline current state is rooted in WorkflowRun

Pipeline current state SHALL be represented and decided by a WorkflowRun aggregate containing ordered StageRuns, Tasks, Checks, approval snapshots, failure reasons, and delivery metadata. Issue stage/status remain coarse projections, `stage_executions` and logs remain evidence, and checkpoints remain resume cursors.

#### Scenario: Current state has one runtime root

- **WHEN** a user, API consumer, runner, or recovery path asks where an issue run currently is
- **THEN** the system SHALL answer from WorkflowRun status, currentStage, StageRuns, tasks, checks, approval snapshots, and failure reason
- **AND** it SHALL NOT require consumers to combine issue stage, `tasks.json`, `stage_states`, execution logs, session logs, check suites, and checkpoints to understand current progress

#### Scenario: Stage transition cannot bypass aggregate

- **WHEN** any workflow path wants to start a stage, complete a stage, fail a stage, or advance to the next stage
- **THEN** it SHALL invoke a WorkflowRun or StageRun domain method
- **AND** it SHALL NOT update issue stage/status or WorkflowRun stage status as the business decision source

#### Scenario: Stage organizes tasks and checks

- **WHEN** a stage contains task progress, check results, and approval state
- **THEN** tasks SHALL remain executable work units
- **AND** checks SHALL remain read-only validators
- **AND** approval SHALL remain user decision state rather than executable check side effect

### Requirement: REQ-PM-007 Integrate failures stay local with visible task/check evidence

Integrate SHALL have the same visible runtime lifecycle as other runnable stages, with task failures stopping the stage locally and post-task check failures remaining visible in Integrate. A post-merge health failure SHALL be distinguished from ordinary repairable check failures because merge delivery has already occurred.

#### Scenario: Task failure stops later Integrate work

- **WHEN** `integrate:spec-sync`, `integrate:archive-change`, or `integrate:merge` fails
- **THEN** later Integrate tasks and checks SHALL NOT run
- **AND** the issue SHALL remain in Integrate failure state with the failing task visible

#### Scenario: Final health failure after merge requires manual intervention

- **WHEN** `health:integrate` fails after merge succeeds
- **THEN** the issue SHALL show that merge already happened
- **AND** WorkflowRun SHALL fail with post-merge delivery failure evidence rather than scheduling an automatic fix task
