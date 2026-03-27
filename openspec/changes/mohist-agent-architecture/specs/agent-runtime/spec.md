## ADDED Requirements

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
The system SHALL support creating agent sessions in memory with: a unique ID, an associated issue ID, a message history (AI SDK CoreMessage[]), and a creation timestamp. Sessions SHALL support adding messages. Sessions are NOT persisted to SQLite in M1 — server restart loses all session data.

#### Scenario: Create session
- **WHEN** a new session is created with an issue ID
- **THEN** the system SHALL generate a unique session ID and store it in memory
- **THEN** the session SHALL start with an empty message history

#### Scenario: Append message to session
- **WHEN** a message is appended to a session
- **THEN** the message SHALL be added to the session's in-memory history
- **THEN** the message SHALL be available for subsequent LLM calls

#### Scenario: Close session
- **WHEN** a session is closed
- **THEN** the session SHALL be marked as closed and removed from active sessions

### Requirement: Sub-agent spawning (opencode subprocess)
The system SHALL support spawning opencode as a subprocess from the Main Agent via the spawn_agent tool. M1 implementation: spawn_agent directly spawns `opencode agent --local --message <task>` in the issue's worktree, synchronously waits for completion, and returns stdout/stderr/exit_code. No child LLM loop in M1.

#### Scenario: Spawn and wait
- **WHEN** the Main Agent calls spawn_agent with agent_type, task, and cwd
- **THEN** the system SHALL spawn an opencode subprocess in the cwd
- **THEN** the system SHALL wait for the subprocess to complete
- **THEN** the subprocess output (stdout/stderr/exit_code) SHALL be returned as a tool result

#### Scenario: Sub-agent timeout
- **WHEN** the opencode subprocess exceeds the configured timeout (default 30 minutes)
- **THEN** the system SHALL kill the subprocess
- **THEN** a timeout error SHALL be returned to the Main Agent

#### Scenario: Sub-agent failure
- **WHEN** the opencode subprocess exits with non-zero code
- **THEN** the stderr output and exit code SHALL be returned to the Main Agent
- **THEN** the Main Agent LLM SHALL decide how to handle the failure

### Requirement: LLM provider configuration
The system SHALL support configuring LLM providers via config table (accessed through existing ConfigRepo). The configuration SHALL include: default model in "provider/model-id" format (e.g. "anthropic/claude-sonnet-4"), and per-provider options (baseURL, apiKey). API keys SHALL be detected from environment variables (ANTHROPIC_API_KEY, OPENAI_API_KEY), shared with opencode.

#### Scenario: Load provider config
- **WHEN** Mohist server starts
- **THEN** the system SHALL load llm config from the config table
- **THEN** the system SHALL detect API key from environment variables
- **THEN** the configured model SHALL be used for LLM calls

#### Scenario: Config with proxy
- **WHEN** llm.provider.<id>.options.baseURL is set in config
- **THEN** the system SHALL use that baseURL for the provider's API calls
