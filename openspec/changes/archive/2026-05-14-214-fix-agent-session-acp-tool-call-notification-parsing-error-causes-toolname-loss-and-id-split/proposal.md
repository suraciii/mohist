## Why

ACP tool call notifications can arrive with tool identity and lifecycle identifiers at different payload levels or under `name` rather than `toolName`. Mohist currently normalizes only part of that shape in `AgentSession`, which can emit live events with a blank/unknown tool name and split one real tool invocation into separate started/completed records.

## What Changes

- Normalize ACP `tool_call` and `tool_call_update` notifications in `AgentSession` before observer dispatch and persistence.
- Preserve the best available tool name from nested `toolCall.toolName`, nested `toolCall.name`, top-level `toolName`, top-level `name`, title, payload shape, or metadata before falling back to `unknown`.
- Preserve or assign one stable `toolCallId` per real tool invocation so live `coder_tool_call` events, session stream logs, workflow logs, and transcript replay use the same lifecycle identity.
- Ensure `tool_call_update` events receive the same normalization as `tool_call` events, including completed output and metadata.
- Add regression coverage for ACP notifications whose id/name fields are split across top-level and nested payloads.

## Capabilities

### New Capabilities


### Modified Capabilities

- agent-runtime
- coder-session-tracking
- pipeline-session-events

## Impact

- Affects `packages/cli/src/agent-runtime/agent-session.ts`, especially session update parsing and observer event creation.
- Affects workflow/session observers that persist session events and emit `coder_tool_call` SSE payloads.
- Affects transcript assembly and live session timeline behavior by improving the quality and stability of tool names and tool call ids they consume.
- Requires backend regression tests around ACP tool notification normalization and lifecycle correlation; no API or dependency changes are expected.
