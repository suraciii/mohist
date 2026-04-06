## Why

Currently `AgentRunnerService` enforces single-agent execution via a single `activePromise` slot, despite `maxConcurrentAgents` being defined in configuration and displayed in logs. This limits throughput when users want to process multiple issues simultaneously and wastes idle capacity. Users must wait for one issue's agent to fully complete (including approval gates) before another can start.

## What Changes

- Replace single `activePromise` tracking with `activeAgents` Map supporting concurrent execution
- Honor the existing `maxConcurrentAgents` configuration (default: 8)
- Queue new agent requests when capacity is reached instead of rejecting with error
- Track individual agent status per issueId for pause/cancel/resume operations
- Remove the "Another issue is already running" error on `issue start`

## Capabilities

### New Capabilities

- `agent-pool`: Concurrent agent execution engine supporting multiple simultaneous agent runs within configured limits

### Modified Capabilities

- `http-api`: Update `/issues/:number/start` behavior to queue rather than reject when at capacity

## Impact

- `packages/cli/src/services/agent-runner-service.ts` — core architecture change (Map-based tracking)
- `packages/cli/src/api/issues.ts` — remove single-agent gate, adapt to queue model
- `packages/cli/src/types/index.ts` — may need new types for queue state
