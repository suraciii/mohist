### Requirement: Generic session records a non-empty transcript on the sessionId axis

A generic (agent-launch) AgentSession SHALL record a non-empty, queryable transcript while the agent executes. The runner SHALL translate the installed external agent's session updates into generic session events and deliver them to the server against the session id (the generic axis), so that assistant text replies, tool calls, and usage appear in the persisted transcript. The transcript SHALL be queryable via the session detail page and `GET /api/projects/{projectRef}/agent-sessions/{sessionId}/transcript`, on the `sessionId` axis that is distinct from the issue/workflow session axis (`#47`'s scope).

For a session that the runner truly executed (consumed tokens, called tools, produced assistant output), the transcript turns SHALL NOT be empty: the `messages` and `events` of the turns SHALL reflect what the agent actually did.

#### Scenario: Assistant text appears in the transcript

- **WHEN** the installed agent emits assistant message chunks for a generic session
- **THEN** the runner SHALL deliver them to the server as message deltas against the session id
- **AND** the persisted transcript for that session SHALL contain the assistant text
- **AND** `GET .../agent-sessions/{sessionId}/transcript` SHALL return that assistant text in the turn's messages

#### Scenario: Tool calls appear in the transcript

- **WHEN** the installed agent invokes tools during a generic session
- **THEN** the runner SHALL deliver tool-call started/updated/completed events against the session id
- **AND** the persisted transcript SHALL contain the tool calls with their inputs and outputs
- **AND** the transcript API response SHALL surface the tool calls as non-empty events

#### Scenario: Usage appears in the transcript and session summary

- **WHEN** the installed agent reports token/context usage for a generic session
- **THEN** the runner SHALL deliver usage events against the session id
- **AND** the session summary and transcript SHALL reflect the consumed input/output tokens and tool activity
- **AND** the recorded usage SHALL be consistent with the transcript turns (non-empty transcript whenever usage is recorded)

#### Scenario: Generic transcript is independent of the issue/workflow axis

- **WHEN** a generic AgentSession is executed and observed by its session id
- **THEN** the transcript SHALL be reachable solely by the session id
- **AND** SHALL NOT require an issue/workflow (`workflowRunId` + `sessionName`) lookup to resolve
- **AND** the issue/workflow session transcript behavior SHALL remain unchanged

### Requirement: Follow-up turns record a non-empty transcript on the same session

A follow-up message delivered to an existing generic AgentSession SHALL produce a new turn whose assistant reply, tool calls, and usage are recorded against the same session id and are visible in the session detail page and the transcript API, just like the initial turn. The runner's reuse of a cached ACP session for a follow-up SHALL NOT cause the follow-up turn's content to be lost or recorded as empty.

#### Scenario: Follow-up message yields a visible non-empty turn

- **WHEN** a follow-up message is delivered to an existing generic AgentSession and the agent produces a reply
- **THEN** the runner SHALL deliver the follow-up turn's deltas against the same session id
- **AND** the transcript SHALL contain a new turn whose messages and events are non-empty and match the agent's reply
- **AND** the transcript API SHALL return both the initial and the follow-up turns

#### Scenario: Cached ACP session reuse does not drop follow-up content

- **WHEN** the runner serves a follow-up by reusing a cached ACP session for the generic session
- **THEN** the follow-up turn's assistant text and tool activity SHALL still be delivered against the session id
- **AND** the persisted transcript for that turn SHALL NOT be empty
