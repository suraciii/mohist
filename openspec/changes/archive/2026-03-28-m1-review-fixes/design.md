## Context

M1 agent architecture (mohist-agent-architecture T-001~T-009) is implemented but has 3 bugs and 3 review issues found during code review. These must be fixed before T-010 (end-to-end test) can succeed.

Current state:
- `advance_stage` tool allows LLM to write any stage value to DB, including stages M1 cannot handle (`waiting-design-review`, `waiting-review`)
- Start endpoint error handler uses `'blocked' as any` instead of `IssueStatus.Blocked`
- `status.ts:41` still references old brand name "crawlph"
- LLM config from ConfigService is never passed to `resolveModel()` — `~/.mohist/config.json` LLM settings have no effect
- Pause endpoint clears `activeAgentPromise` reference but doesn't cancel the background agent loop, enabling double-agent execution
- `spawn_agent` tool returns full stdout to LLM without truncation — large opencode output can bloat the LLM context window

## Goals / Non-Goals

**Goals:**
- Prevent LLM from advancing issues to stages M1 cannot handle
- Fix type safety violations
- Fix brand residual
- Make LLM config take effect
- Prevent double-agent execution via pause race condition
- Prevent LLM context window explosion from large subprocess output

**Non-Goals:**
- Redesigning the Stage system (tracked by Stage PBI in backlog)
- Implementing pause/resume for M1 (M2 feature)
- Adding new capabilities or changing the architecture

## Decisions

### D1: advance_stage whitelist in tool layer

**Choice**: Add a hardcoded M1 transition map inside `advance_stage.ts` execute function. Only `draft→designing`, `designing→implementing`, `implementing→done` are allowed.

**Rationale**: The whitelist is the minimal fix for B1. It prevents LLM from reaching `waiting-*` stages (which would permanently stall the issue since M1 has no gate mechanism). The map is a subset of what M2 will need — upgrading is just adding edges.

**Alternative considered**: Reuse `WorkflowService.canTransition()` — rejected because it enforces the old 6-stage linear path which conflicts with M1's 3-stage intent.

### D2: Pause endpoint returns 501 for M1

**Choice**: Replace the pause endpoint body with a 501 response explaining pause is not supported in M1.

**Rationale**: M1 has no cancellation mechanism for the fire-and-forget agent loop. The safest fix is to disable the endpoint entirely. M2 will re-enable it with proper AbortController support.

### D3: LLM config via ConfigService configRepo

**Choice**: Read `llm.model` from the config table (via ConfigRepo) in `server/index.ts`, extract providerID from the model string (e.g. `openai/gpt-4o` → providerID is `openai`), then read `llm.provider.<providerID>.options.baseURL` dynamically. Pass the built LlmConfig through `createIssueRoutes` → start endpoint → `runMainAgent` → `resolveModel`.

**Rationale**: Mohist stores all config in SQLite config table. The design doc mentioned `~/.mohist/config.json` but the actual config system uses ConfigRepo. Aligning to the existing config system avoids introducing a second config source. ProviderID must be derived from the model string rather than hardcoded, since the user may configure any supported provider (anthropic, openai, etc.).

### D4: Stdout truncation at 8000 characters

**Choice**: Truncate opencode subprocess stdout to first 3000 + last 5000 characters when output exceeds 8000 characters total.

**Rationale**: 8000 chars ≈ 2000 tokens, leaving room for tool call overhead within Sonnet's 200K context. First+last preserves the beginning (task description) and end (result/summary) while dropping middle noise.

## Risks / Trade-offs

**[Whitelist too strict]** → LLM may be unable to handle edge cases (e.g., agent needs to go back to `draft`). Mitigation: M1 is fully automatic — edge cases are acceptable; agent can report issues via `add_comment`.

**[Pause disabled]** → Users cannot stop a running agent in M1. Mitigation: Kill the mo-server process. Acceptable for M1.

**[Config key naming]** → Config table keys for LLM config need to be defined (`llm.model`, `llm.provider.<id>.options.baseURL`). These are new keys not in the existing DEFAULT_CONFIG. If not set, behavior is unchanged (default model + env var API key).
