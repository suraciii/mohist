### Requirement: Followup message delivery via SignalR

Server SHALL deliver followup messages to the associated runner by pushing a `ReceiveFollowup` SignalR message carrying a session target and the followup `text` to the runner's active connection. The session target SHALL identify the live session in a way that admits both workflow-shaped sessions (identified by a `workflowRunId` + `sessionName` pair) and generic, non-workflow sessions (identified by a project-scoped session identity with no `workflowRunId`). The runner SHALL locate the active ACP session for the target via `AcpSessionManager` and invoke `connection.prompt()` fire-and-forget — the handler SHALL NOT await turn completion and SHALL NOT maintain a runner-side message queue. Existing workflow-session follow-up delivery SHALL continue to work unchanged.

#### Scenario: Server pushes followup to runner

- **WHEN** the followup API accepts a message for an active session with an online runner
- **THEN** the server SHALL send a `ReceiveFollowup` SignalR message to the runner's connectionId
- **AND** the message payload SHALL include a session target that identifies the live session and the followup `text`
- **AND** the payload SHALL be sufficient for the runner to locate the active ACP session regardless of whether the session is workflow-shaped or generic

#### Scenario: Runner fire-and-forgets prompt

- **WHEN** the runner receives `ReceiveFollowup` for a currently active ACP session
- **THEN** the runner SHALL call `session.connection.prompt({ sessionId, prompt: [{ type: "text", text }] })` without awaiting completion
- **AND** the handler SHALL return immediately so the SignalR receiver is not blocked

#### Scenario: Runner drops followup for unknown session

- **WHEN** the runner receives `ReceiveFollowup` and no active ACP session matches the carried session target
- **THEN** the runner SHALL silently drop the message
- **AND** the runner SHALL NOT throw or crash

#### Scenario: Generic session receives a followup

- **WHEN** the followup API accepts a message for an active generic (non-workflow) `AgentSession`
- **AND** the associated runner is connected
- **THEN** the server SHALL push a `ReceiveFollowup` SignalR message carrying a session target that identifies the generic session (no `workflowRunId` required)
- **AND** the runner SHALL locate the active ACP session for that generic target
- **AND** the runner SHALL invoke `connection.prompt()` fire-and-forget the same way it does for a workflow session

#### Scenario: Workflow session follow-up delivery is preserved

- **WHEN** the followup API accepts a message for an active workflow-shaped session
- **THEN** the server SHALL push a `ReceiveFollowup` message identifying the session by its `workflowRunId` + `sessionName` pair exactly as before this change
- **AND** the runner SHALL deliver the followup to the workflow session with no behavioral change

### Requirement: Followup injection at runLoop iteration boundary

A followup message SHALL be picked up by opencode's running `runLoop` at the next iteration boundary — after the current tool call completes and before the next LLM request — not mid-tool-call. Mohist SHALL NOT cancel, interrupt, or reprompt the running turn to deliver the followup.

#### Scenario: Followup processed after current tool call

- **WHEN** a followup message is written to the opencode session DB while a tool call is in progress
- **THEN** the running `runLoop` SHALL pick up the message at the next iteration boundary (after the current tool call completes)
- **AND** the agent SHALL NOT be canceled or restarted

#### Scenario: Followup wrapped as system-reminder

- **WHEN** the `runLoop` reads the followup message at an iteration boundary and `step > 1`
- **THEN** the message SHALL be wrapped as a `<system-reminder>` instructing the agent to address the user's message and continue

### Requirement: Multiple rapid followups converge

When a user sends multiple followup messages in quick succession, each message SHALL be independently written to the opencode session DB via `createUserMessage()`. The `runLoop` SHALL read all new messages at the next iteration boundary and process them together. The runner SHALL NOT deduplicate, queue, or serialize followup messages.

#### Scenario: Two followups sent within the same tool call

- **WHEN** a user sends followup A and followup B while the agent is executing a tool call
- **THEN** both messages SHALL be written to the opencode session DB independently
- **AND** the `runLoop` SHALL read both messages at the next iteration boundary
- **AND** both messages SHALL be wrapped as `<system-reminder>` entries in the same LLM request

#### Scenario: Followup sent during session compaction

- **WHEN** a followup message is written while the session is undergoing compaction
- **THEN** the message SHALL persist in the opencode session DB
- **AND** the `runLoop` SHALL pick up the message after compaction completes at the next iteration boundary

### Requirement: Followup transcript turns tagged with followup PromptKind

Transcript turns originating from a followup message injection SHALL be recorded with the `followup` PromptKind. The runner SHALL mark the prompt as `followup` kind when delivering to the server's transcript store so that the session page can distinguish followup turns from initial, task, retry, and recovery turns.

#### Scenario: Followup turn visible in transcript

- **WHEN** a followup message results in a new transcript turn
- **THEN** the turn SHALL be persisted with `PromptKind: "followup"`
- **AND** the session page SHALL render the turn as a distinct followup prompt card

#### Scenario: Followup turn flows through existing event pipeline

- **WHEN** the agent processes a followup message and produces text, tool calls, or reasoning
- **THEN** the output SHALL flow through the existing `sessionUpdate` → grain → SSE pipeline without requiring new event types

### Requirement: Followup prompt rejection is non-fatal

If `connection.prompt()` rejects (for example, because the opencode process has crashed), the runner SHALL catch and log the rejection. The rejection SHALL NOT propagate to the SignalR caller or crash the runner. The session SHALL transition to a failed state through the normal liveness probing mechanism.

#### Scenario: opencode process crashes after followup

- **WHEN** a followup `connection.prompt()` rejects because the opencode process has crashed
- **THEN** the runner SHALL catch the rejection
- **AND** the runner SHALL NOT crash or propagate the error to the server
- **AND** the session SHALL enter failed state via normal liveness probing
