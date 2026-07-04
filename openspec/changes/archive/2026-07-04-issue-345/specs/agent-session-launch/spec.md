### Requirement: Generic launch always mints and propagates a non-null AgentSessionId

A product-level generic AgentSession launch (`POST /api/projects/{projectRef}/agents/{agentRef}/sessions`) SHALL mint a session id up front and propagate it as a non-null `AgentSessionId` onto the standalone `AgentJobInput` it submits. The `AgentJobGrain.BuildDispatch` envelope SHALL carry that `AgentSessionId` verbatim (never normalising a non-empty value to null) so the dispatch contract the runner receives always identifies the generic session that owns the run's runtime events. The runner SHALL use the dispatch envelope's `agentSessionId` verbatim as the runtime-events target identity, without re-deriving or inventing a different one.

A generic launch that reaches dispatch MUST NOT produce a dispatch whose `AgentSessionId` is null/whitespace. The session-id-bearing generic axis and the workflow (`workflowRunId` + `sessionName`) axis SHALL remain distinct: a generic (agent-job) dispatch carries a session id and no `workflowRunId`, and the runner's session-target resolution SHALL select the generic axis solely from `ownerKind === "agent-job"` plus a present `agentSessionId`.

#### Scenario: Launch mints a session id and submits a job carrying it

- **WHEN** a caller launches a generic AgentSession from a project-scoped Agent profile with a non-empty prompt
- **THEN** the system SHALL mint a session id before dispatching
- **AND** the submitted `AgentJobInput.AgentSessionId` SHALL equal the minted session id
- **AND** the `WorkDispatch` envelope built for the runner SHALL carry that session id as a non-null `AgentSessionId`

#### Scenario: Runner routes runtime events to the propagated session id

- **WHEN** the runner executes a dispatched agent-job work item whose envelope carried a non-null `AgentSessionId`
- **THEN** the runner SHALL wire that value verbatim into the action context's `agentSessionId`
- **AND** the runner's session-target resolution SHALL resolve to the generic (`sessionId`) target keyed by that value
- **AND** the runner SHALL route every emitted session event to that session id

#### Scenario: Generic axis is selected without a workflow run

- **WHEN** an agent-job dispatch carries a session id and an empty `workflowRunId`
- **THEN** the runner SHALL resolve the generic session target from the `agentSessionId`
- **AND** SHALL NOT require a `workflowRunId`/`sessionName` pair to identify the session

### Requirement: An unresolved generic session target is observable, not silently dropped

When the runner cannot resolve a session target for an agent-job work item (for example, the dispatch envelope's `AgentSessionId` is missing), the runner SHALL make that condition observable (logged) rather than silently discarding every subsequent session event. The runner SHALL NOT route events to a fabricated target, and SHALL NOT swallow the drop in a no-op return that leaves no trace. This guards the launch → dispatch contract so a null-dispatch regression can be diagnosed instead of presenting as "session runs but records nothing".

#### Scenario: Missing AgentSessionId is observable on the runner

- **WHEN** an agent-job work item is dispatched without an `AgentSessionId` (or with a null/whitespace value)
- **THEN** the runner SHALL record the unresolved session target (at least via a log entry)
- **AND** SHALL NOT silently emit events that vanish without any observable trace of the drop

#### Scenario: A non-null AgentSessionId never collapses to a silent drop

- **WHEN** a generic launch dispatches with a non-null `AgentSessionId` and the installed agent emits transcript deltas
- **THEN** the runner SHALL resolve the generic target and SHALL deliver the events to the server against that session id
- **AND** the event flow SHALL NOT be silently short-circuited by a missing session target
