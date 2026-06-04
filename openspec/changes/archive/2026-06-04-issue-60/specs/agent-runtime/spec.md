# OpenSpec Capability: agent-runtime (delta)

## MODIFIED Requirements

### Requirement: Runner resolves the actual model from ACP session responses

The ACP runner SHALL extract the real model that the agent is using from the ACP `newSession` and `resumeSession` response payloads and from in-session `config_option_update` notifications, separate from the runner-supplied intent model.

#### Scenario: newSession exposes currentModelId
- **WHEN** `connection.newSession` resolves successfully and returns a response whose `models` object includes a non-empty `currentModelId`
- **THEN** the runner SHALL treat that value as the resolved model for the session
- **AND** SHALL forward it on `emitSessionStarted` or via a follow-up `agent_session_model_resolved` event so the server can persist `ResolvedModel` on the session row

#### Scenario: resumeSession exposes currentModelId
- **WHEN** `connection.resumeSession` resolves successfully and returns a response with `models.currentModelId`
- **THEN** the runner SHALL treat that value as the resolved model for the resumed session
- **AND** SHALL forward it to the server even if the intent model in the resume request was different

#### Scenario: config_option_update changes the resolved model
- **WHEN** a `config_option_update` session update notification changes `models.currentModelId` to a new value
- **THEN** the runner SHALL emit an event carrying the new resolved model
- **AND** the server SHALL update the session's `ResolvedModel` to the new value
- **AND** the runner SHALL NOT modify the original intent `Model` field

#### Scenario: Missing currentModelId
- **WHEN** `newSession` / `resumeSession` does not return a `currentModelId` (older ACP versions, no models field, or empty value)
- **THEN** the runner SHALL NOT invent a resolved model
- **AND** the server SHALL keep `ResolvedModel` as `null` until a later update supplies one

### Requirement: Runner captures Usage and UsageUpdate from ACP

The ACP runner SHALL extract token usage and cost data from ACP's `Usage` (on `PromptResponse`) and `UsageUpdate` (session update notification) types and forward it to the server as usage events.

#### Scenario: PromptResponse carries per-turn usage
- **WHEN** `connection.prompt` resolves and the resulting `PromptResponse.usage` is populated
- **THEN** the runner SHALL emit an `agent_usage_update` event with the per-turn usage deltas (input, output, total, cached read, thought tokens), the per-turn cost amount + currency, the latest context window `size`, and the latest context window `used` value
- **AND** the event SHALL be sent after the prompt completes but before the next `agent_liveness_status` or `agent_session_terminal` event

#### Scenario: usage_update session notification
- **WHEN** a `usage_update` session update notification arrives during a prompt loop
- **THEN** the runner SHALL classify it as a liveness activity (so the running session is treated as alive)
- **AND** SHALL forward its payload as an `agent_usage_update` event with the same fields as the `PromptResponse.usage` case

#### Scenario: usage_update with partial fields
- **WHEN** a `usage_update` notification contains only some usage fields (e.g. only `used` and `size` without a cost delta)
- **THEN** the runner SHALL forward all present fields
- **AND** SHALL NOT synthesize missing fields

#### Scenario: usage_update classified as liveness activity
- **WHEN** the runner processes a `usage_update` session update
- **THEN** the liveness classifier (`classifyAcpLivenessActivity`) SHALL treat it as a qualifying liveness notification so the session is not falsely marked as probing
- **AND** `usage_update` SHALL be added to `QUALIFYING_LIVENESS_NOTIFICATION_TYPES` (or otherwise classified as activity)

### Requirement: Runner attaches structured failureCategory to terminal events

The ACP runner SHALL include the existing `LivenessFailureReason` value (from the local `LivenessFailureReason` type) on every `agent_session_terminal` event whose status is `failed`, as a structured `failureCategory` field, alongside the existing free-text `failureReason`.

#### Scenario: Failed monitor pass includes failureCategory
- **WHEN** `monitorPrompt` exits with a `probe_timeout` failure
- **THEN** the runner SHALL emit an `agent_liveness_status` event with `failureReason = "probe_timeout"` as today
- **AND** the subsequent `agent_session_terminal` event SHALL include `failureCategory = "probe_timeout"`
- **AND** the existing free-text `failureReason` field SHALL remain present for backward compatibility

#### Scenario: protocol_disconnect maps to a known category
- **WHEN** `monitorPrompt` reports a `protocol_disconnect` failure (ACP protocol response error without `[PROCESS_EXIT]`)
- **THEN** the `agent_session_terminal` event SHALL include `failureCategory = "protocol_disconnect"`

#### Scenario: process_exit maps to a known category
- **WHEN** the ACP runtime process exits unexpectedly during a prompt
- **THEN** the runner SHALL derive `failureCategory = "process_exit"` from the `[PROCESS_EXIT]` marker
- **AND** include it on the terminal event

#### Scenario: Successful terminal omits failureCategory
- **WHEN** the agent run completes successfully
- **THEN** the `agent_session_terminal` event SHALL NOT include `failureCategory` (or SHALL set it to `null`)
- **AND** `failureReason` SHALL be `null`

### Requirement: Runner forwards usage updates as a new agent_usage_update event type

The runner SHALL emit `agent_usage_update` events with a stable JSON payload so the server-side grain can apply per-turn deltas to the session.

#### Scenario: Usage event payload shape
- **WHEN** the runner emits `agent_usage_update`
- **THEN** the payload SHALL include `inputTokens`, `outputTokens`, `totalTokens`, `cachedReadTokens`, `thoughtTokens` as numbers (all optional / nullable)
- **AND** SHALL include `cost.amount` and `cost.currency` when a cost delta is reported
- **AND** SHALL include `contextWindow.size` and `contextWindow.used` when context window data is reported
- **AND** SHALL include `sessionName`, `workId`, `workType`, `stage`, and `acpSessionId` so the server can route the event to the correct session

#### Scenario: emitSessionStarted accepts a resolvedModel
- **WHEN** the runner calls `emitSessionStarted` after `newSession` or `resumeSession`
- **THEN** it SHALL pass the resolved model id (from `models.currentModelId`) in addition to the intent model
- **AND** the server SHALL persist it as `ResolvedModel` on the session row
