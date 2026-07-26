# Review Findings

## P1: Persisted summary duplicates unbounded tool output in AgentSession state

`AgentSessionActivitySummaryReducer.CreatePart` stores each open-turn tool mutation's complete `PayloadJson` in `AgentSessionActivitySummaryPart` (`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionActivitySummaryReducer.cs:45-50` and `99-114`). That state is serialized in `AgentSession.State` through `PersistedActivitySummary`, so a `tool_call.completed` payload containing `rawOutput` is copied into the Session document in addition to the transcript record.

This directly conflicts with the design's bounded-state rule: current-turn reducer state must retain final part metadata only and "never tool payloads or output" (`openspec/changes/issue-468/design.md`, Risks / Trade-offs). Tool output can be arbitrarily large, so this change makes ordinary Session state writes and activity-card reads grow with a tool result even though the summary only needs the final failure flag plus a stable identifier. It undermines the issue's polling and resource-bound goals.

Replace the persisted raw payload with the minimum reducer metadata needed for the final-state calculation, such as `isFailed` on `AgentSessionActivitySummaryPart`; retain the tool identifier and correlation key for replacement/deduplication. Add coverage that sends a completed tool event with a large `rawOutput` and asserts the serialized Session summary state does not contain that output while the tool counts remain correct.

<promise>FAIL</promise>
