## Why

Agent 启动时报错 `InvalidPromptError: messages must not be empty`。新 session 的 `messages` 为空数组，直接传给 Vercel AI SDK 的 `streamText` 导致崩溃。issue 进入 `Blocked` 状态后，reopen 也无法恢复流程（只改 status 不重启 agent）。

## What Changes

- 在 `runAgentLoop` 中，当 `messages` 为空时自动注入初始用户消息，确保 AI SDK 调用合法
- 修复 `reopen` 端点：在内存中有 pausedSession 时自动 resume agent，无 pausedSession 时将 stage 重置为 `Draft` 以允许重新 `start`

## Capabilities

### New Capabilities

- `agent-initial-message`: 确保新 session 首次调用 AI SDK 时 messages 非空，注入合理的初始消息引导 agent 开始工作
- `reopen-resume`: reopen 操作根据当前状态智能恢复流程（resume agent 或重置 stage）

### Modified Capabilities

## Impact

- `packages/cli/src/agent-runtime/agent-loop.ts` — 注入初始消息逻辑
- `packages/cli/src/api/issues.ts` — reopen 端点增加恢复流程逻辑
- `packages/cli/src/services/issue-service.ts` — 可能需要新增 stage 重置方法
