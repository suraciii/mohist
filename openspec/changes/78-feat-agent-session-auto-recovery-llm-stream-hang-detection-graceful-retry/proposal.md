## Why

LLM provider streaming connections (e.g. MiniMax-M2.7) can silently die while the opencode subprocess remains alive — no exit, no error. Mohist currently waits the full session timeout (15–30 min) before killing the process, wasting 10+ minutes per occurrence and losing all in-progress work. In issue/73, a task died at minute 1 but wasn't detected until minute 15. We need idle-based hang detection to recover within 3 minutes, preserving session context via ACP cancel+prompt instead of kill+respawn.

## What Changes

- Add idle-event detection in `runAcpSession` and `createAcpConnection.prompt()`: track time of last ACP event (`sessionUpdate` callback), declare hang when no event arrives within 3 minutes
- Add hang recovery flow: detect idle → WIP commit (5s timeout) → `connection.cancel()` (5s timeout) → 1s cooldown → `connection.prompt()` with recovery hint → resume monitoring. Up to 2 recovery attempts per session round
- Add degradation: if `cancel()` times out (5s), fall back to kill process (existing behavior); if max recoveries exhausted, return failure to ralph-executor
- Add `hang_unrecoverable` failure category to `categorizeFailure()` in ralph-executor (retryable=false, like `timeout`)
- Add workflow_log event types: `acp_session_hang_detected`, `acp_session_recovery_started`, `acp_session_recovery_succeeded`, `acp_session_recovery_failed`
- Add SSE events: `coder_recovery_status` (emitted via EventBus) for real-time UI awareness of recovery attempts

## Capabilities

### New Capabilities

- **acp-hang-recovery**: Idle-based hang detection and graceful retry within an ACP session. Monitors event stream, detects silence, performs WIP commit → cancel → re-prompt recovery loop with configurable idle threshold (default 3 min) and max attempts (default 2).

### Modified Capabilities

- **ralph-task-execution**: Add `hang_unrecoverable` failure category (non-retryable) to `categorizeFailure()` and `FAILURE_CATEGORY_CONFIGS`, for cases where all recovery attempts are exhausted
- **pipeline-session-events**: Add `coder_recovery_status` SSE event type and corresponding EventBus registration for recovery observability
- **agent-session-ui**: Display recovery status events in SessionTimeline when agent is attempting LLM stream recovery

## Impact

- **`packages/cli/src/agent-runtime/acp-session.ts`**: Core change — idle timer in `sessionUpdate` callback, recovery logic wrapping the `prompt()` call in both `runAcpSession` and `createAcpConnection`
- **`packages/cli/src/openspec/ralph-executor.ts`**: Add `hang_unrecoverable` to `FailureCategory` union, `FAILURE_CATEGORY_CONFIGS`, and `categorizeFailure()`
- **`packages/cli/src/services/event-bus.ts`**: Register `coder_recovery_status` event type
- **`packages/cli/src/api/events.ts`**: Add event type to `ALL_EVENT_TYPES`
- **Frontend**: `agent-events.ts`, `useSSE.ts` — register new event type; SessionTimeline — render recovery status indicators
