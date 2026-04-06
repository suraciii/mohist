## Why

当前存在几个影响核心体验的 bug 和缺陷：

1. **agent_paused 事件丢失**：`AgentRunnerService` 在 agent 完成带 approval 的阶段后会 emit `agent_paused` 事件，但 SSE 端点和 Web UI 都没有注册这个事件类型，导致用户在 Web UI 中看不到审批提示，必须手动刷新页面。这破坏了 gate 审批的核心交互流程。
2. **Skip 按钮无功能**：Web UI Issue 详情页的 Skip 按钮没有 onClick handler，是死按钮。
3. **CLI formatStage 使用旧阶段名**：`cli/commands/issue.ts` 的 `formatStage()` 函数仍使用 `designing`/`implementing` 等旧名，与实际的 `plan`/`build`/`check` 不匹配。
4. **CLI 命令缺少 server 守卫**：`requireServer()` 已定义但未在任何 CLI 命令中调用，server 未启动时用户看到原始的 ECONNREFUSED 错误。

这些问题不涉及新功能开发，但直接影响现有功能的可用性。修复它们是后续 M2 交互能力（ask_user、mo attach）的基础——如果基础事件推送都有问题，新功能只会叠加更多断裂点。

## What Changes

- SSE 端点注册 `agent_paused` 事件类型，确保推送到 Web 客户端
- Web UI 注册 `agent_paused` 事件处理，触发 React Query 刷新
- Web UI types 补充 `agent_paused` 事件类型定义
- Skip 按钮添加 onClick handler（调用 reject/skip API）或移除按钮
- CLI `formatStage()` 更新为当前阶段名（plan/build/check）
- CLI 命令添加 `requireServer()` 守卫，server 不可用时给出友好提示

## Capabilities

### New Capabilities

_(none)_

### Modified Capabilities

- `event-bus`: `agent_paused` 事件从 SSE 端点正确推送到客户端
- `web-ui`: Web UI 正确响应 agent 暂停状态，用户无需手动刷新即可看到审批提示
- `cli-interface`: CLI 命令在 server 不可用时给出友好错误提示；阶段名显示正确

## Impact

- `api/events.ts`: `ALL_EVENT_TYPES` 数组添加 `agent_paused`
- `web/src/hooks/useSSE.ts`: 注册 `agent_paused` 事件处理
- `web/src/lib/types.ts`: 添加 `agent_paused` 类型定义
- `web/src/components/IssueDetailPage.tsx`: Skip 按钮添加 handler 或移除
- `cli/commands/issue.ts`: `formatStage()` 更新阶段名，添加 `requireServer()` 调用
- `cli/commands/project.ts`: 添加 `requireServer()` 调用
- `cli/commands/quick.ts`: 添加 `requireServer()` 调用
