### Requirement: Launch generic AgentSession from an Agent profile

The system SHALL provide a product-level launch path that starts a generic `AgentSession` from a project-scoped `Agent` profile. The launch SHALL resolve the Agent by its project-scoped identity (agent id or name), combine the Agent's `Instructions` and `AgentConfig` with the caller-supplied prompt, optionally merge context references, and execute the prompt via a standalone AgentJob that records a generic `AgentSession` (transcript + runtime events). The launch response SHALL return the new session id, the agent identity, and the current session status so a caller can observe the session the same way workflow sessions are observed.

The launch SHALL NOT require a `WorkflowRun`. Generic `AgentSession` open, attach, and runtime-event回流 SHALL NOT depend on a `workflowRunId`/`sessionName` pair.

#### Scenario: Launch resolves the Agent and combines instructions, config, and prompt

- **WHEN** a caller requests a launch with a project-scoped agent identity and a non-empty prompt
- **THEN** the system SHALL resolve the Agent definition in that project
- **AND** SHALL combine the Agent's `Instructions`, `AgentConfig`, and the caller's prompt into the execution input
- **AND** SHALL execute the prompt via a standalone AgentJob
- **AND** SHALL return the new `AgentSession` id, the agent id/name, and the current session status

#### Scenario: Launch records an observable AgentSession

- **WHEN** a launch executes the prompt via a standalone AgentJob
- **THEN** the system SHALL create an `AgentSession` carrying transcript turns and runtime events
- **AND** the session SHALL be observable through the existing session read paths (transcript, runtime events) the same way workflow sessions are

#### Scenario: Launch does not require a workflow run

- **WHEN** a caller launches a generic `AgentSession` without any `WorkflowRun`
- **THEN** the launch SHALL succeed
- **AND** the resulting `AgentSession` SHALL NOT carry a workflow run reference
- **AND** the session SHALL remain reachable by its session id

#### Scenario: Unknown agent identity is rejected

- **WHEN** a caller requests a launch with an agent identity that does not resolve to an Agent in the project
- **THEN** the launch SHALL be rejected with a clear not-found error
- **AND** no `AgentSession` SHALL be created

#### Scenario: Empty prompt is rejected

- **WHEN** a caller requests a launch with an empty or whitespace-only prompt
- **THEN** the launch SHALL be rejected with a clear validation error
- **AND** no AgentJob SHALL be submitted and no `AgentSession` SHALL be created

### Requirement: AgentJob consumes an Agent definition

Standalone `AgentJob` execution SHALL consume an `Agent` definition rather than only a raw prompt. The AgentJob execution input SHALL carry an agent source (`AgentId`) that identifies the resolved Agent profile. When an Agent definition is supplied, the job SHALL combine the Agent's `Instructions` and `AgentConfig` with the caller's prompt so the installed external agent receives the composed execution input rather than the bare prompt. The job SHALL still be dispatchable with only a raw prompt when no Agent definition is supplied.

The AgentJob dispatch payload SHALL carry an owner kind that distinguishes agent-job work from workflow work, so an agent-job (no `workflowRunId`) and a workflow can never collide on the same in-flight session key.

#### Scenario: AgentJob input carries an agent source

- **WHEN** a standalone AgentJob is submitted for an Agent profile
- **THEN** the job input SHALL carry the resolved `AgentId`
- **AND** the dispatched work SHALL combine the Agent's `Instructions` and `AgentConfig` with the caller's prompt
- **AND** the installed external agent SHALL receive the composed execution input

#### Scenario: AgentJob dispatch distinguishes agent-job from workflow

- **WHEN** the runner receives a dispatch for an agent-job owned work item
- **THEN** the dispatch SHALL carry an owner kind identifying the work as agent-job
- **AND** the runner SHALL scope in-flight tracking by owner kind plus owner identity plus work id
- **AND** an agent-job work item (no `workflowRunId`) and a workflow work item SHALL never collide on the same in-flight session key, even if their work ids matched

#### Scenario: Raw-prompt-only AgentJob remains supported

- **WHEN** a standalone AgentJob is submitted with only a raw prompt and no Agent definition
- **THEN** the job SHALL remain dispatchable
- **AND** the installed external agent SHALL receive the raw prompt as the execution input

### Requirement: Generic AgentSession identity and metadata

A generic `AgentSession` SHALL be identified by its session id without depending on a `WorkflowRun`. The session metadata SHALL record a `source-kind` label of `agent-launch` to distinguish generic sessions from workflow sessions (`source-kind = "workflow"`). The session metadata SHALL also record the agent id and agent name that produced the session.

Optional context references (issue, epic, project, repository, workspace path) supplied at launch SHALL be recorded as session metadata/prompt context only. They SHALL NOT create scope, mount, supervisor, or workflow lifecycle relationships, and SHALL NOT be required to open, attach, or stream runtime events for the session.

The existing workflow-shaped `AgentSession` lookup keys (`project-id`, `workflow-run-id`, `session-name`) SHALL continue to identify workflow sessions unchanged.

#### Scenario: Generic session carries the agent-launch source kind

- **WHEN** a generic `AgentSession` is created from an Agent profile launch
- **THEN** the session metadata SHALL include a `source-kind` label of `agent-launch`
- **AND** the session metadata SHALL include the agent id and agent name

#### Scenario: Generic session is independent of a workflow run

- **WHEN** a generic `AgentSession` exists
- **THEN** the session SHALL be reachable by its session id
- **AND** reads SHALL NOT require a `workflowRunId` lookup key
- **AND** the session SHALL NOT carry a workflow run reference in its metadata

#### Scenario: Optional context references are metadata only

- **WHEN** a launch supplies optional context references such as an issue, epic, project, repository, or workspace path
- **THEN** the references SHALL be recorded in the session metadata as prompt context
- **AND** the references SHALL NOT create scope, mount, or supervisor lifecycle
- **AND** the references SHALL NOT be required to open, attach, or stream runtime events for the session

#### Scenario: Workflow sessions keep their existing lookup keys

- **WHEN** an `AgentSession` belongs to a `WorkflowRun`
- **THEN** the session metadata SHALL continue to carry the existing `project-id`, `workflow-run-id`, and `session-name` lookup keys
- **AND** the existing workflow-session read paths SHALL remain unchanged

### Requirement: Runner supports generic session targets

The runner SHALL identify a live session by a generalized session target that admits both workflow-shaped sessions (carrying a `workflowRunId` + `sessionName` pair) and generic sessions (carrying only a project-scoped session identity, no `workflowRunId`). Generic session open, attach, and runtime-event streaming SHALL NOT require a `workflowRunId` or a workflow-shaped `sessionName` pair.

The runner's in-memory session manager key, its context-derived session target, the runner-to-server session methods, and the follow-up target resolution SHALL all generalize off the `workflowRunId` axis so that a generic session is a first-class target everywhere a workflow session is. Workflow-shaped session behavior SHALL be preserved unchanged.

#### Scenario: Runner opens a generic session without a workflow run

- **WHEN** the runner executes an agent-job work item that has no `workflowRunId`
- **THEN** the runner SHALL open, attach, and stream runtime events for the resulting session using a project-scoped session identity
- **AND** the runner SHALL NOT require a `workflowRunId`/`sessionName` pair to identify the live session

#### Scenario: Runner keys live sessions without colliding across shapes

- **WHEN** the runner has both a workflow session and a generic session live at the same time
- **THEN** the runner's session manager SHALL keep distinct entries for each
- **AND** lookups SHALL return the correct entry for either shape

#### Scenario: Workflow session behavior is preserved

- **WHEN** the runner executes a workflow work item that carries a `workflowRunId` and `sessionName`
- **THEN** the runner SHALL open, attach, and stream runtime events exactly as before this change
- **AND** the in-memory session key and server session calls for workflow sessions SHALL remain unchanged

#### Scenario: Runner streams runtime events for a generic session

- **WHEN** the installed external agent emits transcript turns or runtime events for a generic session
- **THEN** the runner SHALL deliver those events to the server against the generic session identity
- **AND** the events SHALL flow through the existing session-event pipeline without requiring new event types

### Requirement: Minimal cancel and terminate semantics

The system SHALL provide a cancel/terminate operation for a generic `AgentSession`. If the underlying external agent cannot be cancelled, or the session is already in a terminal state, the operation SHALL return that state explicitly rather than pretending success. The operation SHALL NOT silently report a terminal session as cancelled, and SHALL NOT report success when the agent cannot be cancelled.

#### Scenario: Cancel a cancellable active session

- **WHEN** a caller requests cancellation for an active generic session whose underlying agent supports cancellation
- **THEN** the system SHALL attempt to cancel the running turn
- **AND** the response SHALL reflect the resulting session state

#### Scenario: Non-cancellable agent is reported honestly

- **WHEN** a caller requests cancellation for an active session whose underlying agent does not support cancellation
- **THEN** the system SHALL return a state indicating the session is not currently cancellable
- **AND** the response SHALL NOT pretend the cancellation succeeded

#### Scenario: Terminal session reports its terminal state

- **WHEN** a caller requests cancellation for a session that is already in a terminal state (completed, failed, stopped)
- **THEN** the system SHALL return the current terminal state
- **AND** the response SHALL NOT report a fresh cancellation
