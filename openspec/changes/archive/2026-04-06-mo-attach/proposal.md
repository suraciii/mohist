## Why

当前用户在 `mo issue start` 后完全看不到 agent 在做什么——没有进度输出、没有日志流、没有实时反馈。唯一的反馈方式是打开 Web UI 刷新页面，或者等 agent 结束后看 comments。

`mo attach` 是一个 CLI 命令，让用户在终端中实时监控 agent 执行过程。它是用户感知度最高的 M2 功能，也是 "能交互" 的基础——用户需要先能看到 agent 在做什么，才能决定是否需要介入。

初始版本（read-only）主要消费已有的 SSE 事件流，只需要一个必要的后端修复（添加 `agent_paused` 事件订阅）。交互版本（stdin 消息注入）依赖 message-injection change。

## What Changes

- 新增 `mo attach` CLI 命令
- 连接 server SSE 端点，订阅事件流
- 将事件格式化输出到终端（使用 chalk 着色）
- 支持 `--project` 过滤和 `--follow` 自动重连（简单重连，不带断点续传）
- 优雅处理 SIGINT/SIGTERM 退出
- 后端修复：将 `agent_paused` 添加到 SSE 事件订阅列表

## Capabilities

### New Capabilities

- `mo-attach`: CLI 命令，连接 server SSE 端点，实时渲染 agent 执行事件到终端

### Modified Capabilities

- `api/events`: 添加 `agent_paused` 到 `ALL_EVENT_TYPES` 数组（必要修复）

## Impact

- `cli/commands/attach.ts`: 新增命令文件
- `cli/index.ts`: 注册 attach 命令
- `src/api/events.ts`: 添加 'agent_paused' 到事件订阅列表（1 行修改）
