# OpenSpec Capability: coder-session-tracking (delta)

## MODIFIED Requirements

### Requirement: Coder sessions persist resolved model separately from intent model

The persisted `WorkflowAgentSession` SHALL expose both the intent `Model` and a new `ResolvedModel` field. `ResolvedModel` carries the actual model the agent runtime ended up using (extracted from ACP `newSession` / `resumeSession` `models.currentModelId` or from in-session `config_option_update` notifications), while `Model` continues to record the originally requested intent model. The two fields SHALL be independently nullable and SHALL be surfaced as separate fields on `WorkflowAgentSessionDto`, `WorkflowAgentSessionSummaryDto`, and `WorkflowAgentSessionInfoDto`.

#### Scenario: New session resolves to ACP-reported model
- **WHEN** a new ACP session is created and `newSession` returns `models.currentModelId = "anthropic/claude-sonnet-4-20250514"`
- **THEN** the persisted session row stores `Model = "anthropic/claude-sonnet-4-20250514"` (intent) and `ResolvedModel = "anthropic/claude-sonnet-4-20250514"` (resolved) when those values match
- **AND** `ResolvedModel` is exposed alongside `Model` in the session DTOs

#### Scenario: Resolved model differs from intent model
- **WHEN** the runner requested `Model = "openai/gpt-4o"` but the ACP runtime reports `models.currentModelId = "anthropic/claude-sonnet-4-20250514"` after `newSession`
- **THEN** `Model = "openai/gpt-4o"` and `ResolvedModel = "anthropic/claude-sonnet-4-20250514"` are both persisted
- **AND** both values are returned to API consumers without one overwriting the other

#### Scenario: In-session config_option_update switches the resolved model
- **WHEN** a session receives a `config_option_update` notification that changes `models.currentModelId` mid-session
- **THEN** the session row's `ResolvedModel` is updated to the new model id
- **AND** the previous intent `Model` field is NOT modified by the config update

#### Scenario: Missing resolved model keeps the field null
- **WHEN** neither `newSession` / `resumeSession` nor any `config_option_update` reports a current model id
- **THEN** `ResolvedModel` SHALL be `null` and SHALL NOT fall back to the intent `Model`

### Requirement: Coder sessions persist token usage and cost

Persisted `WorkflowAgentSession` records SHALL store the cumulative token usage, cost data, and latest context-window snapshot reported by ACP so users can inspect resource consumption per session.

#### Scenario: Session row carries token usage columns
- **WHEN** a session row is persisted
- **THEN** the row exposes nullable `InputTokens`, `OutputTokens`, `TotalTokens`, `CachedReadTokens`, `ThoughtTokens`, `CostAmount`, `CostCurrency`, `ContextWindowUsed`, and `ContextWindowSize` columns
- **AND** all columns default to `null` for sessions with no recorded usage

#### Scenario: Token usage accumulates across turns
- **WHEN** multiple `agent_usage_update` events for the same session carry per-turn usage deltas
- **THEN** the session row's `InputTokens` / `OutputTokens` / `TotalTokens` / `CachedReadTokens` / `ThoughtTokens` are the running sums of the deltas
- **AND** negative or non-monotonic deltas SHALL NOT cause values to decrease

#### Scenario: Cost amount and currency are stored together
- **WHEN** an `agent_usage_update` event carries `cost.amount` and `cost.currency`
- **THEN** `CostAmount` accumulates the cost and `CostCurrency` records the most recent currency code
- **AND** cost from sessions with mixed currencies SHALL preserve the latest currency code as `CostCurrency`

#### Scenario: Context window usage tracks latest reported value
- **WHEN** an `agent_usage_update` event reports `used` tokens relative to `size` (context window)
- **THEN** `ContextWindowUsed` is set to the latest `used` value reported
- **AND** `ContextWindowSize` is set to the latest `size` value reported when present
- **AND** both fields SHALL be exposed as nullable absolute token counts, not percentages

#### Scenario: Token and cost fields are exposed on session DTOs
- **WHEN** an API consumer requests a session or session list
- **THEN** `WorkflowAgentSessionDto`, `WorkflowAgentSessionSummaryDto`, and `WorkflowAgentSessionInfoDto` each carry the new token usage, cost, and context window fields
- **AND** consumers that do not read these fields continue to work unchanged because all fields are additive

### Requirement: Coder sessions persist structured failure category

Persisted `WorkflowAgentSession` records SHALL store a structured `FailureCategory` alongside the existing free-text `FailureReason` so analytics can group session failures by cause.

#### Scenario: Failure category recorded on session failure
- **WHEN** the runner emits an `agent_session_terminal` event with `status = "failed"` and `failureCategory = "probe_timeout"`
- **THEN** the session row stores `FailureCategory = "probe_timeout"`
- **AND** `FailureReason` continues to record the underlying human-readable error text
- **AND** the two fields are exposed as independent fields on `WorkflowAgentSessionDto` and `WorkflowAgentSessionSummaryDto`

#### Scenario: Failure category is null for non-failure terminals
- **WHEN** an `agent_session_terminal` event reports `status = "completed"`
- **THEN** `FailureCategory` SHALL be `null` and SHALL NOT be derived from `FailureReason`

#### Scenario: Allowed failure category values
- **WHEN** `FailureCategory` is populated
- **THEN** it SHALL be one of the `LivenessFailureReason` enum values emitted by the runner: `probe_timeout`, `probe_send_failed`, `protocol_disconnect`, `process_exit`
- **AND** other values SHALL be rejected by the grain or normalized to the closest matching value

### Requirement: Coder sessions persist tool call and tool error counts

Persisted `WorkflowAgentSession` records SHALL expose cumulative tool call counts so users can spot sessions with unusual tool usage without scanning event logs.

#### Scenario: Tool call count accumulates from tool_call events
- **WHEN** `AppendEventsAsync` receives a `tool_call` event whose payload parses as a tool call (i.e. `ParseToolCall` returns a non-null projection)
- **THEN** the session row's `ToolCallCount` is incremented by 1
- **AND** rows with `tool_call_update` events SHALL NOT increment `ToolCallCount` (they update an existing tool call)

#### Scenario: Tool error count increments on terminal-failed tool updates
- **WHEN** a `tool_call_update` event arrives with status `failed` (or other terminal error status) and parses as a tool call
- **THEN** the session row's `ToolErrorCount` is incremented by 1
- **AND** `ToolErrorCount` SHALL NOT exceed `ToolCallCount` for the same session

#### Scenario: Tool counts exposed on session DTOs
- **WHEN** an API consumer requests a session summary or detail
- **THEN** `WorkflowAgentSessionDto` and `WorkflowAgentSessionSummaryDto` carry `ToolCallCount` and `ToolErrorCount` as integer fields
- **AND** older clients that do not read the fields continue to work

### Requirement: Coder session grain updates from agent_usage_update events

`WorkflowAgentSessionGrain.AppendEventsAsync` SHALL handle `agent_usage_update` events by extracting usage and cost deltas and applying them to the session domain model.

#### Scenario: Usage event updates token counters
- **WHEN** an `agent_usage_update` event payload contains `usage.inputTokens`, `usage.outputTokens`, `usage.totalTokens`, `usage.cachedReadTokens`, `usage.thoughtTokens`, `cost.amount`, `cost.currency`, `size`, and `used`
- **THEN** the grain SHALL call a domain transition (e.g. `ApplyUsage`) that adds the per-turn usage to the session's accumulated token counts, updates `CostAmount` / `CostCurrency`, and sets `ContextWindowUsed` / `ContextWindowSize` to the reported context-window values
- **AND** the event SHALL also be persisted to `WorkflowAgentSessionEvents` so the raw usage timeline remains auditable

#### Scenario: Usage event with only a subset of fields
- **WHEN** an `agent_usage_update` event payload contains only some of the optional usage fields (e.g. only `inputTokens` and `outputTokens`)
- **THEN** the grain SHALL accumulate the present fields
- **AND** missing fields SHALL be treated as zero deltas, not as nulls that erase prior values

#### Scenario: Usage event after terminal
- **WHEN** an `agent_usage_update` event arrives after the session is already in a terminal state
- **THEN** the grain SHALL persist the event row but SHALL NOT mutate the session's terminal state or counters
- **AND** no `agent_usage_update` SSE event is emitted for a terminal session

### Requirement: Coder session grain updates from agent_session_terminal with failureCategory

`WorkflowAgentSessionGrain.AppendEventsAsync` SHALL read the new `failureCategory` field from `agent_session_terminal` payloads and persist it on the session row.

#### Scenario: Terminal event carries failureCategory
- **WHEN** an `agent_session_terminal` event payload includes `failureCategory` and `failureReason`
- **THEN** the grain SHALL set the session's `FailureCategory` to the provided value
- **AND** the existing `FailureReason` (free-text) SHALL remain the underlying diagnostic message

#### Scenario: Terminal event omits failureCategory on success
- **WHEN** an `agent_session_terminal` event payload has `status = "completed"` and no `failureCategory` field
- **THEN** the grain SHALL set `FailureCategory` to `null`
- **AND** `FailureReason` SHALL also be cleared for successful terminals

### Requirement: Coder session grain counts tool calls from AppendEventsAsync

`WorkflowAgentSessionGrain.AppendEventsAsync` SHALL increment persisted `ToolCallCount` and `ToolErrorCount` counters as it appends tool events, reusing the existing `ParseToolCall` projection.

#### Scenario: Tool call start is counted once
- **WHEN** `AppendEventsAsync` processes a `tool_call` event and `ParseToolCall` returns a non-null projection
- **THEN** the grain SHALL increment the session's `ToolCallCount` by 1
- **AND** SHALL NOT increment the counter again for the matching `tool_call_update` for the same tool call id

#### Scenario: Tool call update with failed status counts as an error
- **WHEN** `AppendEventsAsync` processes a `tool_call_update` event with terminal status `failed` (or its equivalents) and `ParseToolCall` returns a non-null projection
- **THEN** the grain SHALL increment the session's `ToolErrorCount` by 1
- **AND** SHALL NOT count successful or in-progress tool updates as errors

#### Scenario: Counters are persisted to the row
- **WHEN** counters are updated during `AppendEventsAsync`
- **THEN** the new `ToolCallCount` and `ToolErrorCount` values SHALL be written to `WorkflowAgentSessions` in the same persistence pass as the appended events
- **AND** the values SHALL survive a Silo restart because they are persisted to storage

### Requirement: EF migration adds observability columns to WorkflowAgentSessions

An EF Core migration SHALL add the new observability columns to the `WorkflowAgentSessions` table without dropping or renaming any existing column.

#### Scenario: Migration creates nullable columns
- **WHEN** the migration runs against an existing SQLite database
- **THEN** it SHALL add `ResolvedModel TEXT NULL`, `InputTokens INTEGER NULL`, `OutputTokens INTEGER NULL`, `TotalTokens INTEGER NULL`, `CachedReadTokens INTEGER NULL`, `ThoughtTokens INTEGER NULL`, `CostAmount REAL NULL`, `CostCurrency TEXT NULL`, `ContextWindowUsed INTEGER NULL`, `ContextWindowSize INTEGER NULL`, `FailureCategory TEXT NULL`, `ToolCallCount INTEGER NULL`, `ToolErrorCount INTEGER NULL` columns
- **AND** SHALL NOT add a default value, NOT NULL constraint, or unique index for any of these columns

#### Scenario: Migration is additive only
- **WHEN** the migration runs
- **THEN** it SHALL NOT rename, drop, or change the type of any existing column
- **AND** existing rows SHALL retain their prior values and gain `null` for the new columns

### Requirement: WorkflowAgentSession domain and DTO surface observability fields

`WorkflowAgentSession` domain, `WorkflowAgentSessionRow` storage, and the related DTO records SHALL expose the new observability fields with consistent names.

#### Scenario: Domain model exposes observability fields
- **WHEN** `WorkflowAgentSession` is loaded or constructed
- **THEN** it SHALL expose `ResolvedModel`, `InputTokens`, `OutputTokens`, `TotalTokens`, `CachedReadTokens`, `ThoughtTokens`, `CostAmount`, `CostCurrency`, `ContextWindowUsed`, `ContextWindowSize`, `FailureCategory`, `ToolCallCount`, `ToolErrorCount` as nullable (or zero-default for counters) properties
- **AND** SHALL provide domain transitions `UpdateResolvedModel(string? model)`, `ApplyUsage(usageDelta, costDelta, contextWindowUsed, contextWindowSize)`, and `RecordToolCall(bool isError)` that mutate the new fields

#### Scenario: Read model DTOs expose observability fields
- **WHEN** `WorkflowAgentSessionQueryService` materializes session DTOs
- **THEN** `WorkflowAgentSessionDto`, `WorkflowAgentSessionSummaryDto`, and `WorkflowAgentSessionInfoDto` each carry the new observability fields with stable JSON property names (`resolvedModel`, `inputTokens`, `outputTokens`, `totalTokens`, `cachedReadTokens`, `thoughtTokens`, `costAmount`, `costCurrency`, `contextWindowUsed`, `contextWindowSize`, `failureCategory`, `toolCallCount`, `toolErrorCount`)
- **AND** fields absent from the underlying row SHALL be emitted as `null` rather than omitted
