## ADDED Requirements

### Requirement: coder_session table stores model, coder_type, and stage
The `coder_session` table SHALL include three additional nullable columns:
- `model TEXT` — the LLM model identifier used for this session (e.g., "deepseek-v4-pro", "glm-5.1")
- `coder_type TEXT` — the coder implementation name (e.g., "opencode")
- `stage TEXT` — the pipeline stage that created this session (e.g., "plan", "build", "review")

Migration v15 SHALL add these columns via ALTER TABLE. Existing rows SHALL have NULL values for these columns.

#### Scenario: New coder session with full metadata
- **WHEN** a coder session is created during the Build stage using model "deepseek-v4-pro"
- **THEN** the `coder_session` row has `model: 'deepseek-v4-pro'`, `coder_type: 'opencode'`, `stage: 'build'`

#### Scenario: Existing coder sessions have NULL for new columns
- **WHEN** migration v15 runs on a database with existing coder_session rows
- **THEN** those rows have `model: NULL`, `coder_type: NULL`, `stage: NULL`

### Requirement: CreateCoderSessionData includes model, coderType, and stage
The `CreateCoderSessionData` interface SHALL be extended with optional fields `model?: string`, `coderType?: string`, and `stage?: string`. The `CoderSessionRepo.insert()` method SHALL write these fields to the database. The returned `CoderSession` interface SHALL include `model: string | null`, `coderType: string | null`, and `stage: string | null`.

#### Scenario: Insert with all new fields
- **WHEN** `coderSessionRepo.insert({ issueId, acpSessionId, executionId, model: 'glm-5.1', coderType: 'opencode', stage: 'plan' })` is called
- **THEN** the database row has `model = 'glm-5.1'`, `coder_type = 'opencode'`, `stage = 'plan'`

#### Scenario: Insert without new fields (backward compatible)
- **WHEN** `coderSessionRepo.insert({ issueId, acpSessionId })` is called without new fields
- **THEN** the database row has `model = NULL`, `coder_type = NULL`, `stage = NULL`

### Requirement: runAcpSession writes model, coderType, stage to coder_session
When `runAcpSession` creates a `coder_session` row (Build stage), it SHALL pass:
- `model`: resolved from `options.model` or the opencode config's model setting
- `coderType`: `'opencode'`
- `stage`: `'build'`

#### Scenario: Build stage session with configured model
- **WHEN** `runAcpSession` is called during Build stage and the opencode config specifies model "deepseek-v4-pro"
- **THEN** the created `coder_session` row has `model: 'deepseek-v4-pro'`, `coderType: 'opencode'`, `stage: 'build'`

### Requirement: createAcpConnection writes model, coderType, stage to coder_session
When `createAcpConnection` creates a `coder_session` row (Plan/Review stage), it SHALL pass:
- `model`: resolved from `options.model` or the opencode config's model setting
- `coderType`: `'opencode'`
- `stage`: `options.stage` (provided by WorkflowController, e.g., 'plan', 'review')

#### Scenario: Plan stage session
- **WHEN** `createAcpConnection` is called during Plan stage with `options.stage: 'plan'`
- **THEN** the created `coder_session` row has `stage: 'plan'`, `coderType: 'opencode'`, and the configured model

### Requirement: coder_text_chunk and coder_tool_call events include coderSessionId and model
The `coder_text_chunk` SSE event payload SHALL be extended with `coderSessionId: string` and `model: string | undefined`. The `coder_tool_call` SSE event payload SHALL be extended with the same fields. These fields identify which coder_session the event belongs to and what model is being used.

#### Scenario: coder_text_chunk with session metadata
- **WHEN** a `coder_text_chunk` event is emitted during a Build session with `coderSessionId: "cs-abc123"` and `model: "glm-5.1"`
- **THEN** the event payload includes `coderSessionId: "cs-abc123"` and `model: "glm-5.1"`

#### Scenario: coder_tool_call with session metadata
- **WHEN** a `coder_tool_call` event is emitted with `coderSessionId: "cs-abc123"` and `model: "glm-5.1"`
- **THEN** the event payload includes `coderSessionId: "cs-abc123"` and `model: "glm-5.1"`

#### Scenario: Events without session context
- **WHEN** a `coder_text_chunk` event is emitted without an associated coder_session (e.g., no `coderSessionRepo` available)
- **THEN** `coderSessionId` is omitted from the payload and `model` is omitted

### Requirement: coder_session_started SSE event emitted on session creation
When a new `coder_session` row is created, the system SHALL emit a `coder_session_started` event with payload: `{ issueId, projectId, coderSessionId, acpSessionId, executionId, model, coderType, stage, taskDescription }`.

#### Scenario: Build stage session started
- **WHEN** `runAcpSession` creates a coder_session for task T-001 during Build stage
- **THEN** `coder_session_started` is emitted with `stage: 'build'`, `taskDescription` containing the task prompt, and `coderSessionId` matching the DB row ID

#### Scenario: Plan stage session started
- **WHEN** `createAcpConnection` creates a coder_session for Plan stage
- **THEN** `coder_session_started` is emitted with `stage: 'plan'`, `taskDescription: null`, and the model name

### Requirement: coder_session_completed SSE event emitted on session completion
When a coder session completes (success or failure), the system SHALL emit a `coder_session_completed` event with payload: `{ issueId, projectId, coderSessionId, status: 'completed' | 'failed', duration: number }`. Duration is calculated as seconds between `createdAt` and `completedAt`.

#### Scenario: Build session completes successfully
- **WHEN** a coder session with `coderSessionId: "cs-abc123"` finishes after 312 seconds
- **THEN** `coder_session_completed` is emitted with `coderSessionId: "cs-abc123"`, `status: 'completed'`, `duration: 312`

#### Scenario: Build session fails
- **WHEN** a coder session fails after 45 seconds
- **THEN** `coder_session_completed` is emitted with `status: 'failed'`, `duration: 45`

## MODIFIED Requirements

### Requirement: Coder session mapping persisted on spawn
When `spawn_coder` tool executes and creates an ACP session, the system SHALL record the mapping of issue_id, acp_session_id, execution_id, a truncated task description, model, coder_type, and stage to the `coder_session` table with status 'running'. The `coder_tool_call` SSE event SHALL additionally carry `rawInput`, `rawOutput`, `title`, `coderSessionId`, and `model` fields so that the WebUI can display tool call details without querying the workflow_log API.

#### Scenario: Spawn coder creates ACP session
- **WHEN** runAcpSession successfully initializes ACP and obtains a sessionId (after `connection.newSession` succeeds)
- **THEN** a coder_session row is created with issue_id (UUID), acp_session_id, execution_id, truncated task (max 200 chars), status='running', created_at, model, coderType='opencode', and stage='build'

#### Scenario: Spawn coder creates ACP session in Plan stage
- **WHEN** createAcpConnection successfully initializes for Plan stage
- **THEN** a coder_session row is created with stage='plan', coderType='opencode', and the configured model name
