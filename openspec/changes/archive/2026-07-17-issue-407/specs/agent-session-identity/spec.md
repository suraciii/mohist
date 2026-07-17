### Requirement: Stable sessionId is the sole AgentSession identity

AgentSession SHALL be the single canonical logical session resource, identified by a stable `sessionId` that never changes for the lifetime of the session. The `sessionId` is the only value API, CLI, and web responses SHALL use to address a session. The origin keys — `workflowRunId + sessionName` for the Workflow source and `agentId` for the Agent-launch source — SHALL be lookup-only references that resolve to the canonical `sessionId`; they MUST NOT be treated as session identity and MUST NOT replace `sessionId` on any command, response, or wire field.

#### Scenario: Source keys resolve to the canonical sessionId, never replace it

- **WHEN** a session command is issued through a Workflow-scoped entry (issue number + session name) or an Agent-launch-scoped entry (agent id + session id)
- **THEN** the system SHALL first resolve the canonical `sessionId` from those origin keys
- **AND** SHALL route the command to that single `sessionId`
- **AND** the response SHALL address the session by the same stable `sessionId`, never by a rotated or freshly minted id

#### Scenario: Same prompt, model, or config never merges sessions of different sources

- **WHEN** two sessions happen to share the same prompt, model, or execution configuration but were created from different sources (one Workflow, one Agent launch)
- **THEN** the system SHALL keep them as distinct AgentSessions with distinct stable `sessionId`s
- **AND** SHALL NOT merge or alias them based on content similarity

### Requirement: Each AgentSession persists exactly one immutable source

Every AgentSession SHALL carry exactly one immutable source, recorded at creation and never changed thereafter. A Workflow source SHALL be identified by `WorkflowRun + sessionName`; an Agent-launch source SHALL be identified by the Agent launch. The source kind (`workflow` or `agent-launch`) SHALL be persisted as a lookup label so the session is reachable by its origin without ambiguity. A session SHALL NOT acquire or switch sources after creation.

#### Scenario: Workflow source is immutable once recorded

- **WHEN** an AgentSession is created from a Workflow run with a given `workflowRunId + sessionName`
- **THEN** that source kind and those origin keys SHALL be recorded once
- **AND** SHALL remain unchanged across compact, reset, follow-up, cancel, and runtime replacements

#### Scenario: Agent-launch source is immutable once recorded

- **WHEN** an AgentSession is created from an Agent profile launch
- **THEN** its `source-kind` SHALL be `agent-launch` with the resolved agent identity recorded once
- **AND** SHALL remain unchanged across all subsequent session operations

### Requirement: Persistent minimal current Runtime Session binding with append-only lineage

Each AgentSession SHALL persist the minimal current Runtime Session binding — `runtime`, `runtimeSessionId`, `runnerId`, and immutable `workDir` — so that session commands survive a Runner process restart and can address the live execution backend. The `runtimeSessionId` identifies the external physical session owned by the named `runtime` execution backend; it is mutable and distinct from the stable `sessionId`. An append-only Runtime Session lineage SHALL record every binding the session has held, so the conversation's runtime history stays auditable. Lineage entries are append-only; existing entries MUST NOT be rewritten or reordered.

#### Scenario: Current binding survives Runner restart

- **WHEN** a Runner process restarts after a session has been bound to a Runtime Session
- **THEN** the persisted current Runtime Session binding (`runtime`, `runtimeSessionId`, `runnerId`, `workDir`) SHALL be available on the next command
- **AND** the command SHALL route to the persisted binding without requiring the caller to re-supply it

#### Scenario: Lineage is append-only across runtime replacements

- **WHEN** a Runtime Session replacement (reset or runtime change) establishes a new `runtimeSessionId` for an AgentSession
- **THEN** a new lineage entry SHALL be appended recording the new binding and its bind time
- **AND** every previously bound entry SHALL remain in the lineage in original order
- **AND** the stable `sessionId` SHALL NOT change

### Requirement: Canonical wire representation uses runtime + runtimeSessionId

The canonical wire representation of the Runtime Session binding SHALL use a `runtime` field naming the execution backend and a `runtimeSessionId` field carrying the external physical session id. The legacy aliases `acpSessionId` and `coderSessionId` MUST be removed from server DTOs, runner payloads, and web clients. No wire field SHALL use `acpSessionId` or `coderSessionId` to carry the physical session identity after this change.

#### Scenario: Server DTOs expose runtimeSessionId, not acpSessionId

- **WHEN** the server serializes a session read model (summary, metadata, workflow session, activity card) to JSON
- **THEN** the physical session id SHALL appear under a `runtimeSessionId` field (or equivalent stable shape)
- **AND** the JSON SHALL NOT contain an `acpSessionId` or `coderSessionId` field carrying the physical session id

#### Scenario: Runner payloads use runtimeSessionId

- **WHEN** the runner emits runtime events or follow-up payloads carrying the physical session id
- **THEN** the payload SHALL address the physical session as `runtimeSessionId`
- **AND** SHALL NOT use `acpSessionId` as the carrying field

### Requirement: Legacy data remains queryable without rewrite and surfaces missing runtime sessions explicitly

Historically persisted AgentSessions and historically rotated session records SHALL remain queryable and auditable without any stored-data rewrite. No migration SHALL mutate, delete, or rewrite existing session state or lineage. A legacy binding whose execution backend no longer exists (for example an ACP binding after an ACP→OpenCode backend replacement) SHALL be treated as "the current Runtime Session does not exist": session operations against it SHALL fail explicitly and prompt a Reset rather than silently fabricating a continuous conversation.

#### Scenario: Legacy sessions stay queryable

- **WHEN** an AgentSession created before this change (including one produced by a historical compact/reset id rotation) is queried
- **THEN** the session SHALL remain reachable and its transcript, lineage, and audit history SHALL be intact
- **AND** no stored data SHALL have been rewritten to make it reachable

#### Scenario: Legacy backend binding surfaces as missing runtime session

- **WHEN** a session command targets an AgentSession whose current Runtime Session binding points at an execution backend that no longer exists (e.g. a legacy ACP binding after backend replacement)
- **THEN** the operation SHALL fail with an explicit "runtime session missing" error
- **AND** the error SHALL prompt a Reset
- **AND** the system SHALL NOT fabricate a synthetic continuous conversation for the missing backend
