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

### Requirement: Session management
The system SHALL support creating agent sessions with: a unique ID, an associated issue ID, a message history (role + content), and a creation timestamp. Sessions SHALL support adding user and assistant messages.

#### Scenario: Create session
- **WHEN** a new session is created with an issue ID
- **THEN** the system SHALL generate a unique session ID and store it in SQLite
- **THEN** the session SHALL start with an empty message history

#### Scenario: Append message to session
- **WHEN** a message (role + content) is appended to a session
- **THEN** the message SHALL be persisted to the session_messages table
- **THEN** the message SHALL be available for subsequent LLM calls

### Requirement: Sub-agent spawning
The system SHALL support spawning sub-agents from a parent session. A sub-agent SHALL have its own independent session and LLM loop. The parent session SHALL synchronously wait for the sub-agent to complete and receive the text result.

#### Scenario: Spawn and wait
- **WHEN** the parent agent calls the spawn_agent tool
- **THEN** the system SHALL create a new child session
- **THEN** the system SHALL run the child agent's LLM loop to completion
- **THEN** the sub-agent's final text output SHALL be returned to the parent as a tool result

#### Scenario: Sub-agent failure
- **WHEN** the sub-agent's LLM loop encounters an error
- **THEN** the error information SHALL be returned to the parent as a tool result
- **THEN** the parent LLM SHALL decide how to handle the failure

### Requirement: LLM provider configuration
The system SHALL support configuring LLM providers via `~/.mohist/config.json`. The configuration SHALL include: default model ID, provider ID, and optional API key. Each agent SHALL be able to override the default model.

#### Scenario: Load provider config
- **WHEN** Mohist server starts
- **THEN** the system SHALL load provider configuration from `~/.mohist/config.json`
- **THEN** the configured model SHALL be used for LLM calls

#### Scenario: Agent-specific model override
- **WHEN** an agent definition specifies a model
- **THEN** that agent SHALL use the specified model instead of the default
