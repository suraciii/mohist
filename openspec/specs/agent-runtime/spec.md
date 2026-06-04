# OpenSpec Capability: agent-runtime

### Requirement: LLM config is loaded from config.jsonc and passed to resolveModel [UPDATED]

The system SHALL read LLM configuration from `~/.mohist/config.jsonc` (NOT from SQLite config table) and pass it to `resolveModel()` so that user-configured model and proxy settings take effect. The old `llm.*` config keys in SQLite are deprecated and ignored.

#### Scenario: LLM model configured in config.jsonc
- **WHEN** `model` is set to "anthropic/claude-sonnet-4-20250514" in config.jsonc
- **THEN** `resolveModel()` SHALL use that model instead of the hardcoded default

#### Scenario: LLM proxy configured in config.jsonc
- **WHEN** `provider.anthropic.baseURL` is set in config.jsonc
- **THEN** `resolveModel()` SHALL create the provider with that baseURL

#### Scenario: No LLM config in config.jsonc
- **WHEN** config.jsonc does not exist or has no `model` field
- **THEN** `resolveModel()` SHALL use the default model (`anthropic/claude-sonnet-4-20250514`)
- **AND** SHALL detect API key from environment variables (ANTHROPIC_API_KEY, etc.)

### Requirement: LLM tool loop

The system SHALL implement an LLM tool loop using Vercel AI SDK v5 `streamText()` with `maxSteps`. The loop SHALL support: tool definition with Zod schema, automatic tool calling cycle (LLM returns tool_call → execute → feed result back → continue), and text generation until LLM stops.

#### Scenario: Tool calling cycle
- **WHEN** the LLM returns a tool_call
- **THEN** the runtime SHALL execute the tool and feed the result back to the LLM
- **THEN** the LLM SHALL continue generating (call more tools or produce text)

#### Scenario: Max steps reached
- **WHEN** the LLM tool calling cycle reaches maxSteps without producing a final text response
- **THEN** the runtime SHALL stop and return the last assistant message

### Requirement: Tool system

The system SHALL provide a tool definition API where each tool is defined with: an id, a description, Zod parameters schema, and an execute function. The execute function SHALL receive validated parameters and return a result string.

#### Scenario: Tool execution with valid parameters
- **WHEN** a tool is invoked with parameters matching its Zod schema
- **THEN** the runtime SHALL validate parameters and call the execute function
- **THEN** the tool result SHALL be returned to the LLM

#### Scenario: Tool execution with invalid parameters
- **WHEN** a tool is invoked with parameters not matching its Zod schema
- **THEN** the runtime SHALL return a validation error to the LLM

### Requirement: In-memory session management

The system SHALL support creating agent sessions in memory with: a unique ID, an associated issue ID, a message history (AI SDK CoreMessage[]), a creation timestamp, and a status (active/paused/closed). Sessions SHALL support adding messages. Sessions SHALL support pause/resume lifecycle transitions. Sessions are NOT persisted to SQLite in M1/M2 — server restart loses all session data.

#### Scenario: Create session
- **WHEN** a new session is created with an issue ID
- **THEN** the system SHALL generate a unique session ID and store it in memory
- **AND** the session SHALL start with status `active` and an empty message history

#### Scenario: Append message to session
- **WHEN** a message is appended to a session
- **THEN** the message SHALL be added to the session's in-memory history
- **AND** the message SHALL be available for subsequent LLM calls
- **AND** the session MUST NOT be closed (both active and paused sessions accept messages)

#### Scenario: Pause session
- **WHEN** a session is paused
- **THEN** the session status SHALL become `paused`
- **AND** the session messages SHALL be preserved
- **AND** the session SHALL be findable via `findByIssueId()`

#### Scenario: Resume session
- **WHEN** a paused session is resumed
- **THEN** the session status SHALL become `active`
- **AND** the session messages SHALL be preserved

#### Scenario: Close session
- **WHEN** a session is closed
- **THEN** the session status SHALL become `closed`
- **AND** the session SHALL NOT accept new messages (appendMessage throws)
- **AND** the session SHALL NOT be findable via `findByIssueId()`

#### Scenario: Find session by issueId
- **WHEN** `findByIssueId(issueId)` is called
- **THEN** the system SHALL return the session with matching issueId that is active or paused
- **AND** closed sessions SHALL NOT be returned
- **AND** if no matching session exists, return undefined

### Requirement: LLM provider configuration [UPDATED]

The system SHALL support configuring LLM providers via `~/.mohist/config.jsonc` (NOT via SQLite ConfigRepo). The configuration SHALL include: default model in "provider/model-id" format (e.g. "anthropic/claude-sonnet-4-20250514"), and per-provider config (apiKey, baseURL, sdk). API keys SHALL be detected from: 1) config.jsonc `provider.<id>.apiKey` (priority), 2) environment variables (ANTHROPIC_API_KEY, OPENAI_API_KEY, etc.).

#### Scenario: Load provider config from config.jsonc
- **WHEN** Mohist server starts
- **THEN** the system SHALL load config.jsonc via ConfigLoader
- **THEN** the system SHALL detect API key from config.jsonc or environment variables
- **THEN** the configured model SHALL be used for LLM calls

#### Scenario: Config with proxy in config.jsonc
- **WHEN** `provider.<id>.baseURL` is set in config.jsonc
- **THEN** the system SHALL use that baseURL for the provider's API calls

#### Scenario: Deprecated SQLite llm.* config ignored
- **WHEN** SQLite config table contains `llm.model` or `llm.provider.*` keys
- **THEN** these keys SHALL be ignored
- **AND** the system SHALL use config.jsonc exclusively for LLM configuration

### Requirement: Model discovery does not create opencode sessions

Model discovery SHALL list available opencode models without creating ACP sessions or persistent opencode session records. Discovery SHALL return model identifiers in `provider/model` format and cache successful results for 30 minutes.

#### Scenario: Discover models through lightweight CLI

- **WHEN** available opencode models are requested
- **THEN** Mohist runs the lightweight `opencode models` command
- **AND** parses returned `provider/model` identifiers
- **AND** does not call ACP `newSession()`

#### Scenario: Discovery cache is fresh for 30 minutes

- **WHEN** model discovery succeeds
- **THEN** subsequent requests within 30 minutes return the cached model list
- **AND** do not spawn another discovery process

#### Scenario: Discovery command fails

- **WHEN** `opencode models` fails or returns no parseable model list
- **THEN** the discovery service reports an error to callers
- **AND** logs the failure for diagnosis

### Requirement: REQ-AR-001 Session liveness probing

Agent runtime SHALL track opencode ACP session liveness using session data timestamps and SHALL probe the same session after a quiet threshold before declaring the session failed.

#### Scenario: New ACP data keeps session running
- **WHEN** a running session receives any valid ACP/opencode session update, assistant text, tool update, message growth, or successful protocol response
- **THEN** `lastDataAt` SHALL be updated
- **AND** the session SHALL remain or return to `running`

#### Scenario: Quiet running session enters probing
- **WHEN** a running session has no valid new data for the configured quiet threshold
- **THEN** the session SHALL transition to `probing`
- **AND** Mohist SHALL send a probe to the same opencode session
- **AND** `probeSentAt` and `probeDeadlineAt` SHALL be recorded

#### Scenario: Probe receives data
- **WHEN** a probing session receives any valid ACP/opencode data before the probe deadline
- **THEN** the session SHALL transition back to `running`
- **AND** the task attempt SHALL continue waiting for normal completion

#### Scenario: Probe fails
- **WHEN** probe sending fails, the probe deadline expires, the ACP protocol disconnects, or the process exits unexpectedly
- **THEN** the session SHALL transition to `failed`
- **AND** the session call result SHALL include `success=false`, session failure metadata, and a failure reason

#### Scenario: Cancellation remains distinct
- **WHEN** the session is actively cancelled by user or abort signal
- **THEN** the session SHALL transition to `cancelled`
- **AND** the result SHALL NOT be classified as session liveness failure

### Requirement: REQ-AR-214 ACP tool notifications are normalized before observer dispatch

Agent runtime SHALL normalize `tool_call` and `tool_call_update` ACP session updates before session observers receive `onSessionEvent`, `onRawNotification`, or `onToolCall` callbacks.

#### Scenario: Top-level tool identity is preserved
- **WHEN** an ACP tool notification carries `toolName`, `name`, `toolCallId`, `id`, or `callId` at the top level instead of inside `toolCall`
- **THEN** the normalized update SHALL expose the best available `toolCall.toolName`
- **AND** SHALL expose a canonical `toolCall.toolCallId`

#### Scenario: Nested and provider ids are preferred
- **WHEN** an ACP tool notification carries a provider id in nested or top-level `toolCallId`, `id`, or `callId`
- **THEN** Agent runtime SHALL reuse that id as the canonical `toolCall.toolCallId`
- **AND** SHALL NOT replace it with a synthetic id

#### Scenario: Missing id is synthesized once
- **WHEN** an ACP tool notification has no provider id
- **THEN** Agent runtime SHALL synthesize one stable `toolCallId` for the notification lifecycle
- **AND** SHALL use that same id for persisted updates and emitted tool-call observer events

#### Scenario: Tool call updates are normalized
- **WHEN** a `tool_call_update` notification is received
- **THEN** it SHALL go through the same identity normalization as `tool_call`
- **AND** completed output and metadata SHALL remain available to observers and logs
