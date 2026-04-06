## Why

当前的交互模型中，用户只能在两个时机介入：
1. 启动前（`mo issue start`）
2. Gate 审批点（`POST /approve`，发送固定消息）

用户无法在 agent 暂停时发送自由文本消息——比如 "等一下，方向不对，改用 PostgreSQL" 或 "先别 build，plan 里的方案 2 更好"。

`message-injection` 让用户可以在 agent session 暂停时（gate 审批点或 ask_user 等待回复时）发送任意消息。消息会被追加到 session 中，触发新的 LLM loop 迭代。

这是 ask-user 的补充——ask_user 是 agent 主动提问，message-injection 是用户主动插话。

## What Changes

- 新增 `POST /api/issues/:number/messages` API 端点
- 支持在 agent 暂停时注入用户消息到 session
- 注入消息后自动 resume agent session（启动新 loop）
- mo attach 扩展为交互式（stdin 输入 → API 调用）
- Web UI 添加消息输入区域

## Capabilities

### New Capabilities

- `message-injection`: 用户可以在 agent 暂停时发送自由文本消息，消息注入到 session 并触发 agent 继续执行

### Modified Capabilities

- `http-api`: 新增 `POST /api/issues/:number/messages` 端点
- `mo-attach`: 从 read-only 扩展为交互式，支持 stdin 消息输入
- `agent-runtime`: AgentRunnerService 支持自由文本 resume（不限于固定的 approve 消息）

## Impact

- `api/issues.ts`: 新增 messages 端点
- `services/agent-runner-service.ts`: resume 方法支持任意消息（已有，当前只被 approve 使用）
- `cli/commands/attach.ts`: 扩展为交互式，添加 readline stdin 处理
- `web/src/components/IssueDetailPage.tsx`: 添加消息输入区域
- `web/src/hooks/useQueries.ts`: 添加 message mutation hook
