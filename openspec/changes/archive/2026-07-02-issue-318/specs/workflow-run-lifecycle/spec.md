## ADDED Requirements

### Requirement: WorkflowRunStatus enumeration captures a single waiting object per status

The `WorkflowRunStatus` enumeration SHALL consist of exactly the values `Created`, `Pending`, `Ready`, `Running`, `AwaitingApproval`, `Paused`, `Stopped`, `Completed`, `Failed`. Each value SHALL denote exactly one scheduling "waiting object" and responsible party, per the following contract:

- `Created` — the workflow run has been built but not started; the initial state.
- `Pending` — started, has dispatchable work, no runner assigned; waiting for *any* runner to claim it. Responsibility lies with runner pool capacity.
- `Ready` — a runner is assigned and dispatchable work exists, but no work is in flight; waiting for *its bound runner* to pick up work. Responsibility lies with the bound runner.
- `Running` — at least one work item is in flight and executing; waiting for nothing.
- `AwaitingApproval` — blocked waiting for an approval decision.
- `Paused` — blocked by an explicit pause.
- `Stopped`, `Completed`, `Failed` — terminal.

The enum MUST NOT encode the old conflated `Running` semantics, and `Pending` MUST NOT mean "built but not started" (that meaning moves to `Created`).

#### Scenario: Enum values match the defined vocabulary

- **WHEN** the `WorkflowRunStatus` enumeration is defined
- **THEN** it SHALL contain exactly `Created`, `Pending`, `Ready`, `Running`, `AwaitingApproval`, `Paused`, `Stopped`, `Completed`, `Failed`
- **AND** no other value SHALL exist

#### Scenario: Pending denotes unassigned and waiting for claim

- **WHEN** a workflow run is in `Pending`
- **THEN** the run SHALL have dispatchable work and no assigned runner
- **AND** its responsible party SHALL be the runner pool (capacity)

#### Scenario: Ready denotes assigned and waiting for pickup

- **WHEN** a workflow run is in `Ready`
- **THEN** the run SHALL have an assigned runner and dispatchable work with no work in flight
- **AND** its responsible party SHALL be the bound runner

#### Scenario: Running denotes in-flight execution

- **WHEN** a workflow run is in `Running`
- **THEN** the run SHALL have at least one in-flight work item executing

### Requirement: WorkflowRun status transitions follow the single state machine

Every write to a workflow run's status SHALL follow the state machine below. No other transition is valid.

- `Created` —[Start]→ `Pending`
- `Pending` —[AssignRunner]→ `Ready`
- `Ready` —[StartTask, i.e. pick work]→ `Running`
- `Running` —[CompleteTask with remaining dispatchable work]→ `Ready` (natural re-readiness)
- `Running` —[Advance with no pending work]→ `Completed` or advance to the next stage
- Any executing state —[Pause]→ `Paused`
- `Paused` —[Resume]→ `Running` if in-flight work exists, else `Ready`
- Any executing state —[request approval]→ `AwaitingApproval`
- `AwaitingApproval` —[Approve]→ `Ready`
- Any executing state —[Fail]→ `Failed`
- Any executing state —[Stop]→ `Stopped`

Every `run.Status =` assignment site SHALL be audited to land on the status prescribed by this machine for its command; an off-machine write is a defect. The sticky-assignment binding is carried by the `Assignment.RunnerId` field, NOT by the status value.

#### Scenario: Start moves Created to Pending

- **WHEN** a `Created` workflow run is started
- **THEN** its status SHALL transition to `Pending`
- **AND** it SHALL NOT transition directly to `Running` or `Ready`

#### Scenario: AssignRunner moves Pending to Ready

- **WHEN** a runner is assigned to a `Pending` workflow run that has dispatchable work
- **THEN** its status SHALL transition to `Ready`
- **AND** the bound-runner relationship SHALL be carried by the `Assignment.RunnerId` field

#### Scenario: StartTask moves Ready to Running

- **WHEN** a `Ready` workflow run picks up a work item
- **THEN** its status SHALL transition to `Running`

#### Scenario: CompleteTask with remaining work returns to Ready

- **WHEN** a `Running` workflow run completes an in-flight work item
- **AND** dispatchable work remains
- **THEN** its status SHALL transition back to `Ready`

#### Scenario: CompleteTask with no remaining work advances or completes

- **WHEN** a `Running` workflow run completes an in-flight work item
- **AND** no dispatchable work remains
- **THEN** its status SHALL advance to the next stage or transition to `Completed`

#### Scenario: Resume lands on Running or Ready by in-flight state

- **WHEN** a `Paused` workflow run is resumed with in-flight work
- **THEN** its status SHALL transition to `Running`
- **WHEN** a `Paused` workflow run is resumed with no in-flight work but with dispatchable work
- **THEN** its status SHALL transition to `Ready`

#### Scenario: Approval moves AwaitingApproval to Ready

- **WHEN** an `AwaitingApproval` workflow run is approved
- **THEN** its status SHALL transition to `Ready`

### Requirement: Status is persisted as a queryable STORED computed column

The `WorkflowRuns` table SHALL expose a `status` STORED computed column derived from the persisted `State` JSON (e.g. `json_extract(State, '$.status')`), normalized so that enum-value casing differences do not fragment the column. The column SHALL always reflect the workflow run's current `WorkflowRunStatus`, so that the scheduler can filter on `status` at the database layer without deserializing the `State` payload into memory.

#### Scenario: status column mirrors the enum value

- **WHEN** a workflow run's `State` is persisted with `status = "Ready"`
- **THEN** the `WorkflowRuns.status` computed column SHALL evaluate to the normalized form of `Ready`
- **AND** the column SHALL be updated automatically whenever `State` changes

#### Scenario: Enum casing is normalized

- **WHEN** the `State.status` JSON value differs only in case from the canonical enum form
- **THEN** the `status` computed column SHALL still match the canonical form

### Requirement: Runner scheduling queries filter by status at the database layer

`FindAssignableAsync` SHALL return exactly the workflow runs whose `status` is `Pending`. `FindAssignedToAsync` SHALL return exactly the workflow runs whose `status` is `Ready` AND whose assigned runner is the queried runner. Neither query SHALL deserialize the full `State` payload of non-matching rows into memory, nor SHALL it apply an in-memory status/assignment re-filter loop. Because the `Ready` filter already excludes in-flight work, every row returned SHALL be directly pickup-able.

#### Scenario: FindAssignableAsync returns only Pending runs

- **WHEN** the scheduler queries for assignable workflow runs
- **THEN** `FindAssignableAsync` SHALL filter at the database layer on `status = Pending`
- **AND** SHALL NOT return runs in `Created`, `Ready`, `Running`, `AwaitingApproval`, `Paused`, `Stopped`, `Completed`, or `Failed`

#### Scenario: FindAssignedToAsync returns only Ready runs for the runner

- **WHEN** a runner queries for work assigned to it
- **THEN** `FindAssignedToAsync` SHALL filter at the database layer on `status = Ready AND assigned runner = <runner>`
- **AND** SHALL NOT return runs in `Running`, terminal states, or `Ready` states bound to a different runner

#### Scenario: Queries do not deserialize non-matching rows

- **WHEN** the database contains workflow runs across many statuses
- **THEN** the scheduling queries SHALL return only matching rows
- **AND** non-matching rows SHALL NOT be deserialized into the application process

### Requirement: Runner poll loop picks up Ready workflows without a busy pre-check

The runner grain's assigned-or-assignable poll loop SHALL NOT perform a `GetCurrentWorkIdAsync` (or equivalent busy/in-flight) pre-check before polling a workflow run surfaced by `FindAssignedToAsync`. Because `Ready` already excludes in-flight work, every surfaced workflow run SHALL be polled directly via `PollWorkAsync`.

#### Scenario: Ready workflows are polled directly

- **WHEN** the poll loop iterates a workflow run surfaced by `FindAssignedToAsync`
- **THEN** the loop SHALL call `PollWorkAsync` directly
- **AND** SHALL NOT first call `GetCurrentWorkIdAsync` to decide whether the run is busy

### Requirement: Historical workflow runs are reclassified to their true new status

When the new state machine is deployed, every persisted workflow run SHALL be reclassified to the correct new status using its assignment and in-flight-work facts, not by blindly preserving the old `Running` label. A historical run that was `Running` with no runner assignment SHALL become `Pending`; one that had an assignment and no in-flight work SHALL become `Ready`; one that had in-flight work SHALL become `Running`; one that was unstarted SHALL become `Created`.

#### Scenario: Historical unassigned Running becomes Pending

- **WHEN** a persisted workflow run carried the old `Running` status with no runner assignment
- **THEN** the migration SHALL reclassify it to `Pending`

#### Scenario: Historical assigned-idle Running becomes Ready

- **WHEN** a persisted workflow run carried the old `Running` status with a runner assignment and no in-flight work
- **THEN** the migration SHALL reclassify it to `Ready`

#### Scenario: Historical assigned-busy Running stays Running

- **WHEN** a persisted workflow run carried the old `Running` status with in-flight work
- **THEN** the migration SHALL keep it as `Running`

### Requirement: Web UI distinguishes pending-claim from assigned-waiting

The Web UI SHALL render `WorkflowRunStatus` such that "待分配 runner" (`Pending`) and "已分配待执行" (`Ready`) are visually distinguishable from each other and from `Running`, so that runner-capacity shortage and stuck-runner conditions can be diagnosed separately.

#### Scenario: Pending and Ready are shown as distinct states

- **WHEN** the Web UI renders a workflow run in `Pending`
- **THEN** it SHALL present a status presentation distinct from `Ready` and from `Running`
- **WHEN** the Web UI renders a workflow run in `Ready`
- **THEN** it SHALL present a status presentation distinct from `Pending` and from `Running`
