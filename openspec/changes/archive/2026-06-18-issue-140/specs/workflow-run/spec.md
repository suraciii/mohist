## ADDED Requirements

### Requirement: Task lifecycle transitions through Running on dispatch

A TaskRun SHALL transition from `Pending` to `Running` when the workflow grain dispatches the task to a runner. The transition SHALL set `StartedAt` to the dispatch time, record the `RunnerId` of the runner that claimed the run, and record the `WorkId` assigned to the dispatch. A `TaskStarted` domain event SHALL be emitted on the `Pending` → `Running` transition. A task SHALL NOT reach `Completed` or `Failed` from `Pending` without first passing through `Running`.

#### Scenario: Dispatched task enters Running

- **WHEN** the workflow grain dispatches a `Pending` task to a runner
- **THEN** the TaskRun SHALL transition to `Running`
- **AND** `StartedAt` SHALL be set to the dispatch timestamp
- **AND** `RunnerId` SHALL be set to the claiming runner
- **AND** `WorkId` SHALL be set to the assigned dispatch work identifier
- **AND** a `TaskStarted(Stage, TaskId, RunnerId)` domain event SHALL be emitted

#### Scenario: Successful result completes a Running task

- **WHEN** a `Running` task receives a successful result
- **THEN** the TaskRun SHALL transition to `Completed`
- **AND** the existing `TaskCompleted` event SHALL be emitted

#### Scenario: Failed result fails a Running task

- **WHEN** a `Running` task receives a failed result
- **THEN** the TaskRun SHALL transition to `Failed`
- **AND** the existing `TaskFailed` event SHALL be emitted

### Requirement: Task lifecycle records completion timestamps

A TaskRun SHALL record `FinishedAt` when it transitions to `Completed` or `Failed`. The timestamp SHALL reflect when the result was processed by the workflow grain, not when the runner locally finished execution. `StartedAt` and `FinishedAt` together SHALL provide an observable dispatch-to-completion duration for every task.

#### Scenario: Completion sets FinishedAt

- **WHEN** a `Running` task transitions to `Completed` or `Failed`
- **THEN** `FinishedAt` SHALL be set to the result-processing time
- **AND** both `StartedAt` and `FinishedAt` SHALL be populated on the terminal TaskRun

### Requirement: TaskRun is the single source of truth for in-flight dispatch

The workflow grain SHALL use TaskRun state as the single source of truth for in-flight task dispatch. Dispatch recovery on grain reactivation, idempotent dispatch decisions, and result matching SHALL read from TaskRun fields (`Status == Running`, `RunnerId`, `WorkId`) rather than a separate lease persistent state. A separate `WorkLease` persistent state SHALL NOT exist on the workflow grain for task dispatch tracking.

#### Scenario: Reactivation restores dispatch from the Running task

- **WHEN** the workflow grain reactivates and a TaskRun is in `Running` state
- **THEN** `RunCoreAsync` SHALL restore the dispatch from the TaskRun's recorded `WorkId` and `RunnerId`
- **AND** the restored dispatch SHALL be re-assigned to the claiming runner

#### Scenario: In-flight check uses Running task instead of lease

- **WHEN** `RunCoreAsync` evaluates whether work is already in-flight
- **THEN** it SHALL detect in-flight work by scanning for a `Running` TaskRun
- **AND** it SHALL NOT read from a separate lease persistent state

#### Scenario: Result matching uses the Running task WorkId

- **WHEN** `ReportResultAsync` receives a result for a `workId`
- **THEN** it SHALL match the incoming `workId` against the `WorkId` of the `Running` TaskRun
- **AND** it SHALL match the reporting `runnerId` against the TaskRun's `RunnerId`
- **AND** a result that does not match the Running task's `WorkId` and `RunnerId` SHALL be ignored

### Requirement: StageCheck carries dispatch metadata for lease-free recovery

A StageCheck SHALL carry `DispatchWorkId`, `DispatchRunnerId`, and `DispatchedAt` fields when dispatched to a runner. The in-flight signal for a dispatched check SHALL be `DispatchWorkId != null && Status == Pending`. StageCheck SHALL NOT gain a `Running` status value or new domain events; its lifecycle SHALL remain `Pending → Passed | Failed`.

#### Scenario: Dispatched check records dispatch metadata

- **WHEN** the workflow grain dispatches a `Pending` check to a runner
- **THEN** `DispatchWorkId`, `DispatchRunnerId`, and `DispatchedAt` SHALL be set on the StageCheck
- **AND** the check `Status` SHALL remain `Pending`

#### Scenario: Check result clears dispatch metadata

- **WHEN** a dispatched check receives a result
- **THEN** the check SHALL transition to `Passed` or `Failed`
- **AND** the dispatch metadata SHALL be cleared

#### Scenario: Reactivation recovers a dispatched check

- **WHEN** the workflow grain reactivates and a check has `DispatchWorkId` set with `Status == Pending`
- **THEN** the workflow SHALL re-dispatch or recover the check based on runner liveness
- **AND** the check SHALL NOT be silently treated as completed
