## ADDED Requirements

### Requirement: WorkflowRun selects work across configured sources

WorkflowRun SHALL remain the authority for selecting the next task, check, approval wait, failure, or completion outcome after stage work has been materialized from configured work sources. Default tasks, dynamic Build tasks, runtime-added tasks, and repair tasks SHALL be represented as ordered StageRun task entities before they are selected for execution.

#### Scenario: Multiple work sources materialize into one StageRun task list

- **WHEN** a stage has default tasks, dynamic tasks, runtime-added tasks, or repair tasks available
- **THEN** WorkflowRun or StageRun SHALL represent them in one ordered task list for that stage
- **AND** `nextWork()` SHALL select executable tasks from that list before selecting checks or approval

#### Scenario: Runtime-added task blocks later checks

- **WHEN** a runtime-added task is pending or running in the current StageRun
- **THEN** WorkflowRun SHALL select that task according to task ordering and dependency rules
- **AND** it SHALL NOT select later checks or approval until the task reaches a successful terminal state

### Requirement: StageRun records source and policy-driven work consistently

StageRun SHALL record task and check state consistently regardless of whether the work came from default stage definitions, Ralph dynamic loading, runtime-added actions, or repair policy scheduling.

#### Scenario: Static and dynamic tasks share task semantics

- **WHEN** a static Plan task, static Integrate task, Ralph Build task, repair task, or runtime-added rebase task is materialized
- **THEN** the task SHALL have a stable id, title, status, order, source or causedBy metadata when applicable, attempts, output, and failure evidence
- **AND** task failure SHALL block later task, check, approval, and stage completion decisions through the same WorkflowRun semantics

#### Scenario: Checks share check semantics

- **WHEN** a check is declared by a stage definition or materialized for persistence
- **THEN** the check SHALL have a stable name, title, status, output, and run evidence
- **AND** check results SHALL be interpreted by WorkflowRun policy rather than by check implementation side effects

### Requirement: Approval is separate from checks in WorkflowRun decisions

WorkflowRun SHALL model approval as a user decision point owned by StageRun state, not as ordinary repairable check work. Runtime-added tasks and invalidation policy MAY cause a stage to leave an approval wait state, but approval SHALL only be invalidated when policy facts require it.

#### Scenario: Approval wait follows successful checks

- **WHEN** a stage requires approval and all required tasks and checks have passed
- **THEN** WorkflowRun SHALL place the StageRun in awaiting approval state
- **AND** it SHALL expose approval as the next workflow decision rather than scheduling a repair task

#### Scenario: Runtime task does not blindly erase approval evidence

- **WHEN** a runtime-added task is appended while a stage is awaiting approval
- **THEN** the StageRun SHALL become runnable so the task can execute
- **AND** prior approval evidence SHALL only be invalidated according to the configured invalidation policy and task result facts

### Requirement: Rebase task reports facts before invalidation decisions

WorkflowRun SHALL treat `rebase-branch` as ordinary task work whose result reports branch facts. Dependent review, check, and approval invalidation SHALL be driven by stage invalidation policy and reported facts rather than by the mere presence of a rebase request.

#### Scenario: Rebase changed snapshot invalidates dependent state

- **WHEN** `rebase-branch` completes successfully and reports that the candidate snapshot changed
- **THEN** WorkflowRun SHALL invalidate the dependent tasks, checks, and approval state declared by the current stage invalidation policy
- **AND** later work SHALL re-run against the new snapshot before approval can pass

#### Scenario: Rebase unchanged snapshot preserves dependent state

- **WHEN** `rebase-branch` completes successfully and reports that the candidate snapshot did not change
- **THEN** WorkflowRun SHALL preserve dependent task, check, and approval state unless another configured invalidation policy applies

#### Scenario: Rebase failure blocks workflow

- **WHEN** `rebase-branch` fails
- **THEN** WorkflowRun SHALL fail the current StageRun through ordinary task failure semantics
- **AND** later tasks, checks, and approval SHALL NOT execute
