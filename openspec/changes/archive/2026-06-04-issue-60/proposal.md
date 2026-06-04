## Why

Session is Mohist's core surface for showing how an Agent executed work, yet today the persisted session record has no token usage, no real model identity, no failure taxonomy, and no live task progress — leaving users unable to trust what model actually ran, how much it cost, why it failed, or how far the underlying tasks have progressed. ACP already exposes all of this data (`Usage`, `UsageUpdate`, `PromptResponse.usage`, `SessionModelState.currentModelId`, `config_option_update`), so the gap is purely an integration miss on the Mohist side and can be closed with a focused observability pass before more advanced session analytics become harder to build.

## What Changes

- **Real resolved model**: Runner extracts `currentModelId` from `newSession` / `resumeSession` responses and listens for `config_option_update` to track in-session model switches; sessions now expose both the intent `Model` and a new `ResolvedModel` field.
- **Token usage and cost capture**: Runner parses ACP `usage_update` notifications and `PromptResponse.usage`, and forwards them as a new `agent_usage_update` session event; sessions now persist `InputTokens`, `OutputTokens`, `TotalTokens`, `CachedReadTokens`, `ThoughtTokens`, `CostAmount`, `CostCurrency`, and `ContextWindowUsed`.
- **Structured failure category**: Runner forwards its existing `LivenessFailureReason` enum (`probe_timeout` / `process_exit` / `protocol_disconnect` / `probe_send_failed`) as a `failureCategory` field on the `agent_session_terminal` event; sessions persist `FailureCategory` alongside the existing free-text `FailureReason`.
- **Tool call statistics**: `WorkflowAgentSessionGrain.AppendEventsAsync` counts `tool_call` / `tool_call_update` events (reusing the existing `ParseToolCall` projection) and persists `ToolCallCount` and `ToolErrorCount` on the session row.
- **Live task progress on activity cards**: `WorkflowAgentSessionQueryService.ToActivityCard` reads current-stage task progress from the `WorkflowRun` projection and fills the existing `ActivityTaskProgressDto` instead of always emitting `null`.
- **API and UI surfacing**: `WorkflowAgentSessionDto`, `WorkflowAgentSessionSummaryDto`, and `ActivityCardDto` carry the new fields; `SessionHeader`, `SessionCard`, and `SessionPage` render resolved-model badge, cumulative token usage, cost, context-window usage, task progress, failure category, and tool counts.
- EF Core migration adds the new columns to `WorkflowAgentSessions`. No breaking changes — added fields are optional and default to existing semantics; the deprecated `Model` (intent) field continues to exist next to `ResolvedModel`.

## Capabilities

### New Capabilities

(none — all behavior fits into existing session/runtime/UI capabilities)

### Modified Capabilities

- `coder-session-tracking`: persisted session records SHALL carry resolved model, token usage breakdown (input / output / total / cached / thought), cost amount + currency, context-window usage, structured failure category, and tool call / tool error counts; the grain SHALL update these from `agent_usage_update`, `agent_session_terminal`, and existing `tool_call*` events.
- `agent-runtime`: ACP runner SHALL extract `currentModelId` from `newSession` and `resumeSession` responses and from `config_option_update` notifications; SHALL parse `Usage` / `UsageUpdate` from `PromptResponse` and `usage_update` notifications; SHALL classify `usage_update` as a liveness activity; SHALL include the existing `LivenessFailureReason` as a structured `failureCategory` on terminal events.
- `pipeline-session-events`: SHALL define a new `agent_usage_update` session event carrying token, cost, and context-window deltas; SHALL extend `agent_session_terminal` with a structured `failureCategory`; both SHALL be registered in the SSE event-type registries so live clients receive them.
- `agent-session-ui`: SessionHeader / SessionCard / SessionPage SHALL render resolved model (distinct from intent model), cumulative usage badges, cost summary, context-window usage, task progress, failure category, and tool counts; activity cards SHALL reflect live task progress instead of an empty placeholder.

## Impact

- **Runner** (`packages/runner/src/actions/acp-agent.ts`): extract `currentModelId`, handle `usage_update`, capture `PromptResponse.usage`, classify usage updates as liveness activity, attach `failureCategory` to terminal events, emit `agent_usage_update`.
- **Server domain / storage** (`packages/server/src/Mohist.Server/Sessions/Domain/WorkflowAgentSession.cs`, `WorkflowAgentSession.Transitions.cs`, `Storage/WorkflowAgentSessionRow.cs`): new fields and transition methods (`UpdateResolvedModel`, `ApplyUsage`, `RecordToolCall`).
- **Server grain** (`Sessions/Grains/WorkflowAgentSessionGrain.cs`): handle `agent_usage_update`, accumulate token / tool counts, persist `FailureCategory` from terminal payload, refresh `ResolvedModel` from `attach_agent` / model-update events.
- **Server queries / API** (`Sessions/Queries/WorkflowAgentSessionReadModels.cs`, `WorkflowAgentSessionQueryService.cs`): expose new fields on DTOs; inject task progress from `WorkflowProjectionService` into `ActivityCardDto`.
- **EF migration** (`packages/server/src/Mohist.Server/Migrations/`): add `ResolvedModel`, `InputTokens`, `OutputTokens`, `TotalTokens`, `CachedReadTokens`, `ThoughtTokens`, `CostAmount`, `CostCurrency`, `ContextWindowUsed`, `FailureCategory`, `ToolCallCount`, `ToolErrorCount` columns to `WorkflowAgentSessions`.
- **Web UI** (`packages/web/src/entities/coder-session/model/types.ts`, `widgets/coder-session/ui/SessionHeader.tsx`, `widgets/coder-session/ui/SessionCard.tsx`, `pages/session/ui/SessionPage.tsx`): consume new DTO fields; render resolved-model badge, usage / cost / context-window indicators, task progress, failure category, tool counts.
- **Backwards compatibility**: All new fields are nullable / additive; existing clients ignore them. No event-type removals. The existing `Model` field stays as the intent model.
