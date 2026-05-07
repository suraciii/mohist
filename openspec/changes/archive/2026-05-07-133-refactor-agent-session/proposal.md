## Why

Agent Session is the core abstraction for running AI coder subprocesses, but it has been incompletely implemented 5 times across 5 mutually unaware modules (`SessionManager`, `agent_session_message` table, `session_stream_log`, `CoderSessionRepo`, `AgentRunnerService.runningSlots`). The 953-line `acp-session.ts` god module duplicates 90% of its code between `runAcpSession` and `createAcpConnection`, and hardcodes business logic (EventBus, workflow repos) into the ACP protocol layer — making it impossible to answer "does issue #42 have a running session?" from a single source of truth.

## What Changes

- **BREAKING**: Remove `SessionManager` class (in-memory `Map`) and all references — it manages sessions that don't correspond to any real process
- **BREAKING**: Remove `agent_session_message` table, repo, migration, and API endpoint — the table exists but no code ever inserts into it
- Merge `AcpSessionOptions` / `AcpConnectionOptions` (14 identical fields) into a single `AgentSessionOptions` type
- Extract `AcpProcessManager` from `acp-session.ts`: subprocess spawn/kill/lifecycle events (~200 lines)
- Extract `AcpProtocolClient` from `acp-session.ts`: initialize/newSession/prompt/cancel, emitting a generic event stream (~250 lines)
- Create `AgentSession` class combining process + protocol layers with `create()` / `execute()` / `close()` / `getState()` (~200 lines)
- Implement `withSession(options, fn)` helper: auto create → execute → close, replacing `runAcpSession`
- Define `SessionObserver` interface (`onTextChunk` / `onToolCall` / `onSessionEvent` / `onStateChange`) and implement `WorkflowSessionObserver` to move all EventBus/repo side-effects out of the ACP layer
- Implement `SessionStateMachine` with typed states (`initializing` | `running` | `completed` | `failed` | `timeout` | `cancelled` | `closed`) and centralized transitions through `coder_session` table
- Promote `coder_session` table to the single source of truth for session state; replace in-memory `runningSlots` / `abortControllers` with DB-backed cache

## Capabilities

### New Capabilities

- `agent-session-abstraction` — Unified `AgentSession` class with factory methods, lifecycle management, and observer-based event dispatch
- `session-state-machine` — Centralized session state transitions with `coder_session` as single source of truth
- `session-observer` — `SessionObserver` interface and `WorkflowSessionObserver` implementation decoupling ACP events from business logic

### Modified Capabilities

- `agent-runtime` — Replace `runAcpSession` / `createAcpConnection` with `AgentSession.create()` / `withSession()`; remove `SessionManager` requirement ("In-memory session management"); update "Sub-agent spawning" to use new session abstraction
- `coder-session-tracking` — Promote `coder_session` to single source of truth; add state machine transitions; remove redundant session state tracking
- `spawn-coder` — Update to use `withSession()` instead of `runAcpSession`; event capture via `SessionObserver` instead of inline handlers
- `main-agent-session-persistence` — Remove entirely (this spec covers the unused `agent_session_message` table which is being deleted)

## Impact

**Core module**: `packages/cli/src/agent-runtime/acp-session.ts` (953 lines) → split into 3–4 files each < 300 lines

**Direct consumers** (20 files importing from `acp-session.ts`):
- `packages/cli/src/workflow/` — `plan-stage-runner.ts`, `check-stage-runner.ts`, `workflow-engine.ts`, `stage-context.ts`, `checks/code-compiles-check.ts`, `checks/build-test-check.ts`
- `packages/cli/src/services/` — `agent-runner-service.ts`, `skill-service.ts`, `explore-acp-service.ts`, `conflict-resolution.ts`
- `packages/cli/src/server/index.ts` — session management for Web UI
- `packages/cli/src/openspec/ralph-executor.ts` — task execution

**Database**: Remove `agent_session_message` table (migration); extend `coder_session` with state machine columns

**Deleted code**: `packages/cli/src/agent-runtime/session.ts` (SessionManager), `packages/cli/src/db/agent-session-message-repo.ts`, related API endpoints

**Tests**: 6 test files reference `runAcpSession` / `createAcpConnection` / `AcpConnection` — all need updates to new API

**Web UI**: No API contract changes — backend APIs remain compatible; `coder_text_chunk` / `coder_tool_call` SSE events continue unchanged
