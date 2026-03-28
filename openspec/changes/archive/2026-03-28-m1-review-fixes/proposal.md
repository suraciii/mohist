## Why

M1 agent architecture implementation (mohist-agent-architecture T-001~T-009) introduced 3 bugs and 3 review issues that must be fixed before T-010 (end-to-end test) can succeed.

## What Changes

- Fix `advance_stage` tool to enforce M1 stage transition whitelist (B1: LLM can jump to any stage, including ones M1 cannot handle like `waiting-design-review`)
- Fix `'blocked' as any` type safety violation in start endpoint error handler (B3)
- Fix crawlph brand residual in status API error message (R6)
- Pass LLM config from ConfigService through to `runMainAgent` / `resolveModel` (R4: config.json LLM settings never take effect)
- Disable pause endpoint for M1 to prevent race condition with fire-and-forget agent loop (B2: clearing promise reference doesn't cancel background work, allowing double-agent execution)
- Add stdout truncation to `spawn_agent` tool to prevent LLM context window explosion (R2)

## Capabilities

### New Capabilities

(none)

### Modified Capabilities

- `workflow-engine`: advance_stage tool now enforces M1 stage transition whitelist instead of allowing arbitrary stage values
- `agent-runtime`: spawn_agent tool now truncates stdout to prevent excessive LLM context usage
- `http-api`: pause endpoint returns 501 for M1; LLM config is threaded through start endpoint to agent runtime

## Impact

- **Code**: `tools/advance-stage.ts`, `tools/spawn-agent.ts`, `api/issues.ts`, `api/status.ts`, `server/index.ts`, `agents/main-agent.ts`
- **Behavior**: LLM can no longer jump stages arbitrarily; pause is explicitly unsupported in M1; LLM config from config table now takes effect
- **Dependencies**: None
