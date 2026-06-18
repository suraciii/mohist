### Requirement: WorkDispatch carries owner-kind dimension

`WorkDispatch` SHALL carry an `owner-kind` dimension that identifies which kind of owner the work belongs to. The two permitted values SHALL be `workflow` and `agent-job`. Owner-kind SHALL drive routing decisions at the Runner coordination layer only; it SHALL NOT alter the execution layer (`WorkExecutor`, ACP agent action, agent session), which remains generic and kind-agnostic.

#### Scenario: workflow owner-kind is the unchanged baseline

- **WHEN** a `WorkDispatch` is constructed for work owned by a WorkflowRun
- **THEN** its `owner-kind` SHALL be `workflow`
- **AND** its `WorkflowRunId` SHALL be populated and used as the owner identity

#### Scenario: agent-job owner-kind is the new additive value

- **WHEN** a `WorkDispatch` is constructed for work owned by an AgentJob
- **THEN** its `owner-kind` SHALL be `agent-job`
- **AND** its `AgentJobId` SHALL be populated and used as the owner identity
- **AND** the dispatch SHALL NOT require a `WorkflowRunId`

#### Scenario: owner-kind does not change execution-layer behavior

- **WHEN** the Runner executes a `WorkDispatch` of either owner-kind
- **THEN** the same `WorkExecutor` + ACP agent action + session path SHALL be used
- **AND** execution SHALL depend only on the dispatch's `uses`, prompt/`with`, variables, and workspace identity

### Requirement: Runner accepts work from either owner identity

`AssignWorkAsync` SHALL accept a `WorkDispatch` whose owner identity is either a `WorkflowRunId` (`workflow`) or an `AgentJobId` (`agent-job`). The Runner SHALL NOT reject work solely because `WorkflowRunId` is absent when `owner-kind` is `agent-job`. The Runner SHALL reject a dispatch only when its owner identity field is missing for its declared `owner-kind`, or when `WorkId` is missing.

#### Scenario: agent-job dispatch without WorkflowRunId is accepted

- **WHEN** `AssignWorkAsync` receives a dispatch with `owner-kind = agent-job`, a non-empty `AgentJobId`, and a non-empty `WorkId`
- **AND** no `WorkflowRunId` is present
- **THEN** the Runner SHALL accept and track the work
- **AND** the Runner SHALL return an `Assigned` result

#### Scenario: workflow dispatch with WorkflowRunId continues to be accepted

- **WHEN** `AssignWorkAsync` receives a dispatch with `owner-kind = workflow`, a non-empty `WorkflowRunId`, and a non-empty `WorkId`
- **THEN** the Runner SHALL accept and track the work using the existing workflow keying
- **AND** the response SHALL be byte-equivalent to the pre-change behavior for the same inputs

#### Scenario: dispatch missing the owner identity for its kind is rejected

- **WHEN** `AssignWorkAsync` receives a dispatch whose `owner-kind` is `workflow` but `WorkflowRunId` is empty
- **OR** whose `owner-kind` is `agent-job` but `AgentJobId` is empty
- **OR** whose `WorkId` is empty
- **THEN** the Runner SHALL reject the dispatch
- **AND** the Runner SHALL return a `Rejected` result with an `invalid-work` reason

### Requirement: IsWorkRunnableAsync routes by owner-kind

The Runner SHALL verify that a piece of assigned work is still runnable by asking the owner grain of the work's `owner-kind`. For `owner-kind = workflow`, the Runner SHALL ask `IWorkflowGrain` and apply the existing liveness rule (claimed runner matches this Runner, run status is `Running`, and current work id matches). For `owner-kind = agent-job`, the Runner SHALL ask `IAgentJobGrain` whether the work is still valid and the job has not been cancelled.

#### Scenario: workflow path asks IWorkflowGrain unchanged

- **WHEN** `IsWorkRunnableAsync` evaluates a dispatch with `owner-kind = workflow`
- **THEN** the Runner SHALL call `IWorkflowGrain.GetClaimedRunnerIdAsync`, `GetRunStatusAsync`, and `GetCurrentWorkIdAsync`
- **AND** the runnable decision SHALL match the pre-change rule exactly

#### Scenario: agent-job path asks IAgentJobGrain

- **WHEN** `IsWorkRunnableAsync` evaluates a dispatch with `owner-kind = agent-job`
- **THEN** the Runner SHALL call `IAgentJobGrain` to determine whether the work is still valid
- **AND** the Runner SHALL treat a cancelled or no-longer-valid job as not runnable

#### Scenario: non-runnable work is dropped from the dequeue loop

- **WHEN** `IsWorkRunnableAsync` returns false for an assigned work item of either owner-kind
- **THEN** the Runner SHALL remove that work from its tracked work map
- **AND** the Runner SHALL continue the dequeue loop without dispatching it

### Requirement: ReportResultAsync routes by owner-kind

`ReportResultAsync` SHALL deliver a work result to the owner grain of the work's `owner-kind`. For `owner-kind = workflow`, results SHALL be delivered to `IWorkflowGrain.ReportResultAsync` and the run status SHALL be read back exactly as before. For `owner-kind = agent-job`, results SHALL be delivered to `IAgentJobGrain.ReportResultAsync`.

#### Scenario: workflow result routing is unchanged

- **WHEN** the Runner reports a result for a dispatch with `owner-kind = workflow`
- **THEN** the Runner SHALL call `IWorkflowGrain.ReportResultAsync(runnerId, workId, result)`
- **AND** SHALL read back the workflow status via `GetRunStatusAsync`
- **AND** the returned `RunnerWorkReportResult` SHALL be byte-equivalent to the pre-change behavior

#### Scenario: agent-job result is delivered to IAgentJobGrain

- **WHEN** the Runner reports a result for a dispatch with `owner-kind = agent-job`
- **THEN** the Runner SHALL call `IAgentJobGrain.ReportResultAsync(runnerId, workId, result)`
- **AND** SHALL NOT call any `IWorkflowGrain` method
- **AND** the Runner SHALL stop tracking the work after a successful report

#### Scenario: report with missing owner identity is rejected

- **WHEN** `ReportResultAsync` is called with an `owner-kind` whose owner identity is empty
- **THEN** the Runner SHALL return a not-tracked / missing-owner result
- **AND** the Runner SHALL NOT invoke any owner grain

### Requirement: AgentJobGrain owns lifecycle and result

`AgentJobGrain` SHALL be the authoritative owner of a standalone agent job's lifecycle and terminal result. A job SHALL progress through the states `pending` -> `running` -> `completed` or `failed`, and SHALL NOT skip the `running` state on a normal execution path. The grain SHALL accept the work result from the Runner through `ReportResultAsync` and SHALL persist that result as the job's owned terminal result.

#### Scenario: pending job becomes running when work is dispatched

- **WHEN** an `AgentJobGrain` in `pending` dispatches its work to a Runner
- **THEN** the job state SHALL transition to `running`
- **AND** the job SHALL record the runner id that accepted the work

#### Scenario: running job becomes completed on success

- **WHEN** a `running` job receives a `ReportResultAsync` call with a successful `WorkResult`
- **THEN** the job state SHALL transition to `completed`
- **AND** the job SHALL own the reported status, message, output, and artifacts as its terminal result

#### Scenario: running job becomes failed on failure

- **WHEN** a `running` job receives a `ReportResultAsync` call with a failed `WorkResult`
- **THEN** the job state SHALL transition to `failed`
- **AND** the job SHALL own the failure status, message, and any output/artifacts as its terminal result

#### Scenario: completed or failed job rejects further reports

- **WHEN** `ReportResultAsync` is called against a job that is already in `completed` or `failed` state
- **THEN** the grain SHALL reject the report
- **AND** the previously owned terminal result SHALL remain unchanged

### Requirement: Agent jobs dispatch directly via RunnerRegistry

An `AgentJobGrain` SHALL dispatch its work directly to an idle Runner discovered via `RunnerRegistry`. The grain SHALL NOT enqueue work into `WorkflowBacklogGrain`, and `WorkflowBacklogGrain` SHALL NOT be modified to know about agent jobs. When no Runner with a free slot is available, the grain SHALL retry dispatch on a backoff schedule until a slot becomes available or a configured retry bound is reached.

#### Scenario: agent job dispatches through RunnerRegistry

- **WHEN** an `AgentJobGrain` in `pending` state looks for a runner
- **THEN** it SHALL query `RunnerRegistry` for an online runner with a free slot
- **AND** it SHALL call `AssignWorkAsync` directly on the chosen `IRunnerGrain` with `owner-kind = agent-job`

#### Scenario: WorkflowBacklogGrain is bypassed

- **WHEN** an `AgentJobGrain` schedules and dispatches its work
- **THEN** the grain SHALL NOT call any method on `IWorkflowBacklogGrain`
- **AND** `WorkflowBacklogGrain` SHALL remain unchanged

#### Scenario: no idle slot triggers backoff retry

- **WHEN** `RunnerRegistry` reports zero runners with a free slot
- **AND** the configured retry bound has not been reached
- **THEN** the job SHALL remain `pending` and retry dispatch after a backoff interval
- **AND** the job SHALL NOT transition to `failed` solely because no slot was momentarily available

#### Scenario: retry bound reached fails the job

- **WHEN** the configured dispatch retry bound is reached without ever acquiring a runner slot
- **THEN** the job SHALL transition to `failed`
- **AND** the failure reason SHALL identify runner unavailability

### Requirement: WorkspaceManager accepts standalone agent-job work

`WorkspaceManager.ensure` SHALL accept a work item whose `owner-kind` is `agent-job` and whose identity is a standalone workspace path or `workDir`, without requiring `repository.gitUrl` or `issue.number`. The existing issue-scoped worktree behavior SHALL remain unchanged for `owner-kind = workflow`.

#### Scenario: agent-job workspace uses provided path

- **WHEN** `WorkspaceManager.ensure` is called for a work item with `owner-kind = agent-job`
- **AND** the work carries a workspace path or `workDir` variable
- **THEN** the manager SHALL ensure that directory exists and return it as the workspace
- **AND** the manager SHALL NOT require `repository.gitUrl` or `issue.number`

#### Scenario: agent-job workspace falls back to a standalone workdir

- **WHEN** `WorkspaceManager.ensure` is called for an `agent-job` work item without a workspace path
- **THEN** the manager SHALL create and return a standalone fallback directory keyed by the work id
- **AND** it SHALL NOT attempt the issue-scoped worktree path

#### Scenario: workflow worktree behavior is unchanged

- **WHEN** `WorkspaceManager.ensure` is called for a work item with `owner-kind = workflow`
- **THEN** the manager SHALL follow the existing issue-scoped worktree resolution
- **AND** the resolved path SHALL be byte-equivalent to the pre-change behavior for the same inputs

### Requirement: Minimal end-to-end validation API

The system SHALL expose a single HTTP endpoint that accepts a request body of `{ prompt, model, workspace }`, creates an `AgentJobGrain` with that input, awaits the job's terminal result, and returns the result to the caller. The endpoint exists solely to validate that the engine runs end-to-end; it is explicitly not the product CLI (`mo agent run <name>`), not the agent entity / named-agent surface, and not the read-model / board / activity projection surface.

#### Scenario: POST creates and awaits a job

- **WHEN** a caller `POST`s `{ prompt, model, workspace }` to the validation endpoint
- **THEN** the system SHALL create a new `AgentJobGrain` in `pending` state
- **AND** the system SHALL wait for the job to reach `completed` or `failed`
- **AND** the response SHALL include the job's terminal status, message, output, and any artifacts

#### Scenario: job failure produces a structured error response

- **WHEN** the created job reaches `failed` state
- **THEN** the endpoint SHALL return the failure status, message, and reason
- **AND** the response SHALL distinguish job failure from request-handling error

#### Scenario: missing required fields are rejected before job creation

- **WHEN** the request body omits `prompt`
- **THEN** the endpoint SHALL reject the request without creating an `AgentJobGrain`
- **AND** the endpoint SHALL return a clear validation error

### Requirement: Workflow coordination path behavior is preserved

For `owner-kind = workflow`, the Runner coordination layer SHALL preserve pre-existing routing, ownership, scheduling, result-reporting, and regression-test semantics exactly. The `owner-kind` dimension SHALL be additive: workflow work SHALL behave as if `owner-kind` did not exist, and the workflow path SHALL NOT depend on any `IAgentJobGrain` code path.

#### Scenario: workflow dispatch and reporting use the existing path

- **WHEN** any combination of `AssignWorkAsync`, `IsWorkRunnableAsync`, `PollAsync`, and `ReportResultAsync` is invoked for `owner-kind = workflow` work
- **THEN** the observed Runner behavior SHALL be byte-equivalent to the pre-change implementation for the same inputs
- **AND** no `IAgentJobGrain` SHALL be constructed or consulted on that path

#### Scenario: workflow regression tests remain green

- **WHEN** the existing `workflow-run`, `workflow-engine`, and `ralph-task-execution` spec scenarios are executed
- **THEN** all pre-change scenarios SHALL continue to pass without modification
- **AND** no existing workflow scenario SHALL be weakened or removed to accommodate agent-job work
