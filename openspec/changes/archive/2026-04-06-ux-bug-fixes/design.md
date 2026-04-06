## Context

M1 完成后，mohist 的核心流程（创建 Issue → 启动 Agent → plan → build → check → gate 审批 → done）已可用。但探索发现多个体验断裂点，最严重的是 `agent_paused` 事件在 SSE 层丢失，导致 Web UI 的 gate 审批流完全依赖手动刷新。

其他问题（旧阶段名、缺少 server 守卫、Skip 按钮死链）是代码演化的遗留，M1 阶段迁移阶段模型和重构 API 时未完全清理。

## Goals / Non-Goals

**Goals:**

- 修复 `agent_paused` 事件从 server 到 Web UI 的完整推送链路
- 修复 Skip 按钮为可用状态（或移除）
- CLI 阶段名显示与实际实现一致
- CLI 命令在 server 不可用时给出友好提示

**Non-Goals:**

- 不新增任何功能（ask_user、mo attach、消息注入等留给后续 change）
- 不改变现有 API 行为
- 不重构 agent 暂停/恢复机制

## Decisions

### D1: agent_paused 补入 SSE ALL_EVENT_TYPES

`api/events.ts` 的 `ALL_EVENT_TYPES` 数组漏了 `agent_paused`。EventBus 已定义该事件（`agent-runner-service.ts` 中 emit），但 SSE 层未转发。

修复：在 `ALL_EVENT_TYPES` 中添加 `'agent_paused'`。

### D2: Web UI 监听 agent_paused 刷新 Issue 状态

`useSSE.ts` 需要注册 `agent_paused` 事件，触发 `invalidateQueries` 刷新 issue 详情和列表。

`types.ts` 的 `SSEEvent` union 需要添加 `agent_paused` 类型。

### D3: Skip 按钮实现为 "拒绝并回退"

当前 `IssueDetailPage.tsx` 的 Skip 按钮无 handler。有两个选项：
- 实现 reject 功能（拒绝当前阶段结果，回退到上一阶段）
- 移除按钮（当前没有 reject API）

**决策**：移除 Skip 按钮。当前没有 reject API（backlog B-110），添加一个半成品按钮比没有更误导。reject 功能留给后续 change。

### D4: CLI formatStage 更新为当前阶段名

`cli/commands/issue.ts:32-38` 的 `formatStage()` 使用旧名 `designing`/`implementing`。更新为 `plan`/`build`/`check`。

### D5: CLI 命令添加 requireServer 守卫

在 `cli/commands/issue.ts`、`cli/commands/project.ts`、`cli/commands/quick.ts` 的命令 handler 入口调用 `requireServer()`。server 不可用时，`requireServer()` 会打印友好错误并 exit。

`requireServer()` 已在 `cli/index.ts:30` 定义，只需在各命令中 import 并调用。

**注意**：`server` 命令本身不应加守卫（它是用来启动 server 的）。`init` 命令也不应加（它不依赖 server）。

## Risks / Trade-offs

- **[Risk] 移除 Skip 按钮可能让用户困惑** → 当前按钮是死链，移除反而更清晰。reject 功能是 M2+ 的事
- **[Risk] requireServer 可能误拦有效命令** → 只在需要 server 的命令（issue/project/quick）中添加，server/init/config 命令不加
- **[Low] 改动面小，全是独立修复** → 每个 bug 修复互不依赖，可以分别验证
