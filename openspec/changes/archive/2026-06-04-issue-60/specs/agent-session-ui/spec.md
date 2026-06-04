# OpenSpec Capability: agent-session-ui (delta)

## MODIFIED Requirements

### Requirement: SessionHeader renders resolved model, usage badges, and tool counts

`SessionHeader` SHALL render the new observability fields exposed by `CoderSessionSummary` / `CoderSessionItem` (resolved model, cumulative token usage, cost, context window usage, and tool counts) in addition to the existing intent model badge.

#### Scenario: Resolved model is shown when it differs from intent model
- **WHEN** `session.resolvedModel` is non-null AND differs from `session.model`
- **THEN** the header SHALL display both the intent `model` and the `resolvedModel` (e.g. `requested: openai/gpt-4o · using: anthropic/claude-sonnet-4-20250514`)
- **AND** when they are equal, the header SHALL display a single model badge to avoid visual noise

#### Scenario: Token usage and cost badges appear when available
- **WHEN** `session.totalTokens` (or any of `inputTokens` / `outputTokens` / `cachedReadTokens` / `thoughtTokens`) is non-null
- **THEN** the header SHALL render a usage badge that shows the cumulative token count (formatted as e.g. `12.4k tokens`) and the cost (e.g. `$0.18`)
- **AND** when cost currency is missing, the cost badge SHALL be omitted rather than showing an empty currency

#### Scenario: Context window usage is shown when reported
- **WHEN** `session.contextWindowUsed` and `session.contextWindowSize` are non-null
- **THEN** the header SHALL render a context-window indicator (e.g. `14k / 200k ctx`) so users can see how much of the model's context the run has consumed
- **AND** when only `contextWindowUsed` is reported (no `size`), the header SHALL show the used count alone (e.g. `14k ctx used`)

#### Scenario: Tool call and tool error counts appear in the header
- **WHEN** `session.toolCallCount` and `session.toolErrorCount` are non-null on the summary
- **THEN** the header SHALL show a small tool-count indicator (e.g. `12 tools · 0 errors`)
- **AND** when `toolErrorCount` is greater than zero, the indicator SHALL be styled as a warning

### Requirement: SessionCard surfaces resolved model, usage summary, failure category, and tool counts

`SessionCard` (the activity feed card) SHALL render the new observability fields exposed by `AgentActivitySession` so users can see model, cost, and progress at a glance.

#### Scenario: Resolved model badge in card header
- **WHEN** the card's `model` differs from `resolvedModel` (both non-null)
- **THEN** the card SHALL show a small "using <resolvedModel>" hint next to the existing model text
- **AND** when they match, the card SHALL continue to show a single model label

#### Scenario: Usage summary line in card
- **WHEN** the session's `inputTokens` / `outputTokens` / `costAmount` are non-null
- **THEN** the card SHALL render a compact usage line beneath the title (e.g. `12.4k in · 3.1k out · $0.18`)
- **AND** the line SHALL wrap cleanly on narrow card widths

#### Scenario: Failure category badge in card
- **WHEN** `failureCategory` is non-null on the card
- **THEN** the card SHALL show a short failure-category chip (e.g. `probe_timeout`) next to the existing failure badge
- **AND** the underlying `failureReason` SHALL still appear in full as a tooltip or expandable detail

#### Scenario: Tool counts in card
- **WHEN** `toolCallCount` is non-null on the card
- **THEN** the card SHALL show a tool count indicator (e.g. `· 8 tools`)
- **AND** when `toolErrorCount` is greater than zero, the indicator SHALL be highlighted (e.g. `· 8 tools · 1 error`)

### Requirement: SessionPage displays session usage and cost summary

`SessionPage` SHALL surface the new observability fields in the session header so the dedicated session page exposes the same information as the activity card.

#### Scenario: Session header shows resolved model
- **WHEN** the metadata DTO carries both `model` (intent) and `resolvedModel` and they differ
- **THEN** the page header SHALL show both values (e.g. `model: openai/gpt-4o (using anthropic/claude-sonnet-4-20250514)`)
- **AND** when equal, the page SHALL show a single model value

#### Scenario: Session header shows token usage and cost
- **WHEN** the metadata DTO carries `inputTokens` / `outputTokens` / `totalTokens` / `cachedReadTokens` / `thoughtTokens` / `costAmount` / `costCurrency`
- **THEN** the page header SHALL display a usage block with token counts and cost
- **AND** SHALL prefer human-readable formatting (e.g. `12.4k in · 3.1k out · $0.18`) over raw integer counts

#### Scenario: Session header shows context window usage
- **WHEN** the metadata DTO carries `contextWindowUsed` and a known context window size (from the latest `agent_usage_update`)
- **THEN** the page header SHALL show the context window indicator (e.g. `14k / 200k ctx`) and a percentage
- **AND** the percentage SHALL be derived client-side from `contextWindowUsed` and `contextWindowSize` and SHALL be capped at 100% for display

#### Scenario: Session header shows failure category
- **WHEN** the metadata DTO carries `failureCategory`
- **THEN** the page header SHALL show a structured failure-category chip alongside the existing free-text failure reason
- **AND** the chip SHALL use a stable label per `LivenessFailureReason` value (`probe_timeout`, `probe_send_failed`, `protocol_disconnect`, `process_exit`)

#### Scenario: Session page stays read-only
- **WHEN** the new observability fields are added to the page
- **THEN** the page SHALL continue to be read-only: no composer, continue input, stop control, or steering control is added
- **AND** the usage/cost context block SHALL live within the existing header area

### Requirement: Activity card exposes live workflow task progress

`ActivityCardDto` SHALL carry a `TaskProgress` field populated from the current `WorkflowRun` stage so the activity feed shows real progress, not a placeholder.

#### Scenario: Activity card shows workflow task progress
- **WHEN** `WorkflowAgentSessionQueryService.ToActivityCard` builds an `ActivityCardDto`
- **THEN** it SHALL look up the workflow run for the session's `WorkflowRunId` and find the current stage and its tasks
- **AND** SHALL set `TaskProgress` to `{ completed, total }` where `completed` is the count of tasks in the current stage with status `completed` (or its equivalents) and `total` is the count of all tasks in that stage
- **AND** if no current stage can be derived, the field SHALL be `null`

#### Scenario: TaskProgress matches what WorkflowProjectionService reports
- **WHEN** `WorkflowProjectionService.ListActiveAgentsAsync` and `WorkflowAgentSessionQueryService.GetActivityAsync` both look at the same workflow run
- **THEN** they SHALL report the same `TaskProgress.completed` / `TaskProgress.total` for sessions in the same stage
- **AND** the query service SHALL reuse the existing `WorkflowQueryService` / `WorkflowProjectionService` accessors rather than recomputing task status from scratch

#### Scenario: Existing `null` placeholder is removed
- **WHEN** the activity endpoint is queried
- **THEN** the response SHALL include a non-null `TaskProgress` for sessions that have a resolvable current stage
- **AND** the only sessions that still get `null` SHALL be those for which the workflow run / stage cannot be loaded

### Requirement: SessionMetadata type carries the new observability fields

The frontend `SessionMetadata` and `CoderSessionSummary` interfaces SHALL include the new observability fields so React components can render them without type errors.

#### Scenario: SessionMetadata fields are typed
- **WHEN** `SessionMetadata` (in `entities/coder-session/model/types.ts`) is used
- **THEN** it SHALL expose `resolvedModel`, `inputTokens`, `outputTokens`, `totalTokens`, `cachedReadTokens`, `thoughtTokens`, `costAmount`, `costCurrency`, `contextWindowUsed`, `contextWindowSize`, `failureCategory`, `toolCallCount`, `toolErrorCount` as nullable strings / numbers
- **AND** `CoderSessionSummary` SHALL expose the same fields so list / card components can read them

#### Scenario: AgentActivitySession carries the new fields
- **WHEN** the activity feed loads `AgentActivity` sessions
- **THEN** `AgentActivitySession` SHALL expose the same observability fields with matching JSON property names (`resolvedModel`, `inputTokens`, `outputTokens`, `totalTokens`, `cachedReadTokens`, `thoughtTokens`, `costAmount`, `costCurrency`, `contextWindowUsed`, `contextWindowSize`, `failureCategory`, `toolCallCount`, `toolErrorCount`)
- **AND** values not yet present in the backend response SHALL default to `null` so older sessions continue to render cleanly
