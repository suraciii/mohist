### Requirement: The AgentJob is the sole authority over its own terminal state

An AgentJob SHALL own its terminal state transitions. A job SHALL start in `Pending`, move to `Running` only when a Runner accepts the dispatch, and reach `Completed` or `Failed` only via the AgentJob grain's result-report path. An AgentJob SHALL reach `Failed` without ever running when its dispatch retry bound is exhausted (runner unavailable), and SHALL reach `Failed` when its report timeout fires while still `Running`. AgentSession runtime events — transcript appends, usage updates, Runtime Session lineage entries, compaction records — SHALL NEVER cause an AgentJob terminal transition. The AgentSession is the logical conversation and audit record; it does not adjudicate the AgentJob.

#### Scenario: A successful turn completes the AgentJob via the result-report path

- **WHEN** the Runner finishes an Agent-owned execution request successfully and reports the result
- **THEN** the AgentJob grain SHALL transition `Running → Completed` via its result-report handler
- **AND** the AgentSession SHALL record the transcript, usage, and lineage events without itself adjudicating the job

#### Scenario: AgentSession events do not terminalize the AgentJob

- **WHEN** the AgentSession emits transcript, usage, or lineage events during a running AgentJob
- **THEN** the AgentJob status SHALL remain `Running`
- **AND** only the AgentJob grain's result-report path SHALL transition it to `Completed` or `Failed`

#### Scenario: Dispatch retry bound exhausted fails the job without execution

- **WHEN** a `Pending` AgentJob cannot acquire a Runner before the configured dispatch bound
- **THEN** the AgentJob grain SHALL transition `Pending → Failed` with a runner-unavailable failure reason
- **AND** SHALL NOT ever have entered `Running`

#### Scenario: Report timeout fails a running job

- **WHEN** a `Running` AgentJob does not receive its result report before the configured job timeout
- **THEN** the AgentJob grain SHALL transition `Running → Failed` with a report-timeout failure reason

### Requirement: An AgentJob drives the shared OpenCodeRuntime directly via an Agent-owned execution request

Execution of an AgentJob SHALL drive the shared `OpenCodeRuntime.runTurn` deep module directly. The Runner SHALL NOT route AgentJob execution through the `mohist/opencode` Action contract, through any `mohist/agent` Action, or through any ACP bridge. The AgentJob execution request SHALL carry a `RuntimeSessionTarget` (`runtime: "opencode"`, optional `runtimeSessionId`, `workDir`), the composed prompt, and optional `options.model` and `options.variant`. When `runtimeSessionId` is null, the runtime SHALL create a new physical Session in `workDir`; when non-null, the runtime SHALL restore the existing physical Session. The Runner SHALL pass the OpenCode runtime handle to the AgentJob execution context with no owner-kind gating.

#### Scenario: The AgentJob calls OpenCodeRuntime directly

- **WHEN** a Runner executes an AgentJob dispatch
- **THEN** the executor SHALL call `OpenCodeRuntime.runTurn` directly with an Agent-owned request
- **AND** SHALL NOT resolve the request through the `mohist/opencode` Action, a `mohist/agent` Action, or any ACP bridge

#### Scenario: A new physical Session is created when no binding exists

- **WHEN** an AgentJob execution request carries a `RuntimeSessionTarget` with `runtimeSessionId: null`
- **THEN** the runtime SHALL create a new physical OpenCode Session in the target `workDir`
- **AND** SHALL return the new `runtimeSessionId` in the turn facts

#### Scenario: An existing physical Session is restored when a binding exists

- **WHEN** an AgentJob execution request carries a non-null `runtimeSessionId`
- **THEN** the runtime SHALL resolve the existing OpenCode Session for that id and `workDir`
- **AND** SHALL run the prompt against the restored Session without creating a new one

#### Scenario: The OpenCode runtime handle reaches the AgentJob path

- **WHEN** the Runner builds the execution context for an AgentJob dispatch
- **THEN** the context SHALL receive the same OpenCode runtime handle as a Workflow dispatch
- **AND** SHALL NOT be gated off from the runtime based on owner kind

### Requirement: The AgentJob execution request is fixed at launch time

The AgentJob execution request — Agent identity, instructions, model/variant, and prompt — SHALL be captured from the resolved Agent definition at launch time. Edits to the Agent definition (instructions, config, status, archived flag) made after the AgentJob is submitted SHALL NOT change the in-flight request, the dispatch payload, or the executing turn. The AgentJob state SHALL persist the launch-time snapshot independently of the live Agent definition.

#### Scenario: Editing the Agent definition does not change a running job

- **WHEN** an Agent's instructions or model/variant are edited while one of its AgentJobs is `Running`
- **THEN** the running AgentJob SHALL continue to use the launch-time-fixed instructions and configuration
- **AND** the dispatch payload SHALL NOT be recomposed from the edited Agent

#### Scenario: Archiving the Agent definition does not change a running job

- **WHEN** an Agent is archived or disabled while one of its AgentJobs is `Pending` or `Running`
- **THEN** the AgentJob SHALL continue dispatch and execution with the launch-time snapshot
- **AND** SHALL NOT be cancelled or failed solely because the Agent definition is no longer active

### Requirement: AgentJob execution is independent of the Workflow Action contract

The AgentJob launch and execution pipeline SHALL NOT consume the `mohist/opencode` Action Input contract (`with.prompt`, `with.session`, `with.options`), the Workflow task completion contract (`expect`, `failIf`, recovery), or the `mohist/acp-agent` Action identity. Changing the Workflow Action name, Input shape, Output shape, completion contract, or recovery semantics SHALL NOT affect Mohist Agent launch or execution. The Runner's WorkDispatch for an AgentJob SHALL NOT carry a `Uses` value that selects a Workflow Action.

#### Scenario: A Workflow Action contract change does not affect Mohist Agent launch

- **WHEN** the `mohist/opencode` Action's Input, Output, completion, or recovery contract changes
- **THEN** Mohist Agent manual launch and subscription dispatch SHALL continue to start AgentJobs and execute them unchanged
- **AND** the AgentJob execution request SHALL NOT pass through the Workflow Action Input contract

#### Scenario: An AgentJob dispatch carries no Workflow Action identity

- **WHEN** the AgentJob grain builds a WorkDispatch for the Runner
- **THEN** the dispatch SHALL NOT select the `mohist/opencode` Action, the `mohist/acp-agent` Action, or any Workflow Action
- **AND** the Runner SHALL route the dispatch to the Agent-owned OpenCode execution path on the owner-kind discriminator

### Requirement: The AgentJob records the runtime session binding reported by the Runner

The Runner SHALL report the physical OpenCode Session id back to the server once the Agent-owned execution request establishes or restores it. The AgentJob grain SHALL record that id on its persisted state via an idempotent binding-recording operation that verifies the reporting Runner, work id, and AgentSession id. A repeated report of the same id SHALL be a no-op. A divergent report SHALL be rejected.

#### Scenario: The Runner records the physical session id

- **WHEN** an Agent-owned execution request establishes or restores a physical OpenCode Session
- **THEN** the Runner SHALL report the `runtimeSessionId` to the server against the AgentJob's AgentSession id
- **AND** the AgentJob grain SHALL persist it on its state

#### Scenario: A repeat binding report is idempotent

- **WHEN** the Runner reports the same `runtimeSessionId` a second time for the same AgentJob
- **THEN** the AgentJob grain SHALL accept it as a no-op
- **AND** SHALL NOT mutate state or append lineage

#### Scenario: A mismatched binding report is rejected

- **WHEN** a binding report arrives with a Runner id, work id, or AgentSession id that does not match the AgentJob's persisted values
- **THEN** the AgentJob grain SHALL reject the report
- **AND** SHALL NOT mutate the persisted runtime session id

### Requirement: The AgentJob result report carries terminal status and payload

The Runner SHALL post the AgentJob result to the server with `ownerKind: "agent-job"` and the AgentJob id, and SHALL omit the `workflowRunId` field. The AgentJob grain SHALL treat the report's `Status` as success when it case-insensitively matches `completed`, `pass`, `ok`, or `success`, and as failure otherwise. On success the grain SHALL record a terminal result and close the generic AgentSession with a `completed` close event; on failure the grain SHALL record the failure reason, the failure category derived from the report, and close the AgentSession with a `failed` close event.

#### Scenario: A success report completes the job and closes the session

- **WHEN** the Runner reports `Status: "completed"` for a `Running` AgentJob
- **THEN** the grain SHALL transition `Running → Completed`
- **THEN** the grain SHALL persist a terminal result carrying status, message, output, exit code, and artifact ids
- **AND** SHALL emit a `completed` close event against the AgentJob's AgentSession

#### Scenario: A failure report fails the job with reason and category

- **WHEN** the Runner reports a non-success `Status` for a `Running` AgentJob
- **THEN** the grain SHALL transition `Running → Failed`
- **AND** SHALL persist the failure reason and derived failure category on the terminal result
- **AND** SHALL emit a `failed` close event against the AgentJob's AgentSession

#### Scenario: A report for an already-terminal job is rejected

- **WHEN** a result report arrives for an AgentJob that is already `Completed` or `Failed`
- **THEN** the grain SHALL reject the report
- **AND** SHALL NOT mutate state or re-close the AgentSession

### Requirement: Each AgentSession runs at most one work-initiated prompt at a time

A logical AgentSession SHALL run at most one work-initiated prompt concurrently, regardless of whether the owner is a TaskRun or an AgentJob. A user Follow-up is a Session command, not a new AgentJob; it SHALL be receivable while a work turn is active without creating a new AgentJob or rotating the session.

#### Scenario: Concurrent work prompts on one session are not allowed

- **WHEN** two work-initiated prompts target the same logical AgentSession concurrently
- **THEN** at most one SHALL execute at a time

#### Scenario: A Follow-up is a Session command, not a new AgentJob

- **WHEN** a user issues a Follow-up against an AgentSession with an active AgentJob turn
- **THEN** the Follow-up SHALL be received as a Session command on the current physical Session
- **AND** SHALL NOT create a new AgentJob
