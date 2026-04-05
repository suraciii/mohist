## Why

mohist 目前只有 CLI 交互方式，用户无法直观地查看 Issue 工作流进度、审批 gate、以及 Agent 运行状态。Web UI 让用户通过浏览器完成所有操作，降低使用门槛，提供实时可视化体验。

## What Changes

- 新增嵌入式 Web UI，`mo server` 同时提供 API 和前端页面
- 看板视图：Issue 按 stage（draft/plan/build/check/done）分列展示
- Issue 详情页：描述、评论、stage 进度条、审批操作、git diff
- 实时推送：通过 **In-Process EventBus + SSE** 推送 stage 变化、评论添加、Agent 状态事件
  - Agent tools（advance_stage、add_comment）在同进程内直接 emit EventBus 事件
  - SSE endpoint 订阅 EventBus 转发给浏览器
- Issue 操作：创建、编辑、关闭/重开、添加评论、审批 gate、启动 Agent
  - Approval Gate 采用 **Stop & Resume 模式**：Agent session 自然结束，用户 Approve 后启动新 session 恢复执行
  - Stage output 通过 comments 持久化，新 session 从 DB 恢复上下文
- 项目切换：多项目之间切换，SSE 事件按项目隔离
- 前端技术栈：React + Vite + Tailwind CSS + TanStack Query
- Agent Runner Service：从 API routes 提取 agent 运行逻辑为独立服务，提供状态查询和生命周期事件

## Capabilities

### New Capabilities
- `embedded-web-ui`: 嵌入式 SPA 构建与静态文件服务（Vite 构建产物嵌入 server）
- `web-ui-kanban`: 看板视图（Issue 按 stage 分列、卡片展示、Agent 运行状态指示）
- `web-ui-issue-detail`: Issue 详情页（描述、评论、stage 进度条、审批操作、git diff）
- `web-ui-issue-actions`: Issue 操作 API（创建、编辑、关闭/重开、审批 gate、启动 Agent）
  - 审批 gate 采用 Stop & Resume 模式（session 自然结束 + 新 session 恢复）
- `web-ui-realtime`: SSE 实时事件推送（stage_changed、comment_added、agent_status、approval_requested）
  - In-Process EventBus 直接从 Agent tools emit 事件
- `web-ui-project-switch`: 项目切换与项目级数据隔离（SSE 事件按项目过滤）

### Modified Capabilities
- `http-api`: 新增 SSE endpoint（带 projectId 参数）、静态文件服务、Issue 操作端点（审批、启动）、Agent 状态端点
- `server-daemon`: server 启动时同时提供 API 和嵌入式前端

## Impact

- `packages/cli/src/server/` — 新增静态文件服务、SSE endpoint
- `packages/cli/src/api/` — 新增/扩展 Issue 操作 API、Agent 状态 API
- `packages/cli/src/agent-runtime/` — 新增 EventBus 模块
- `packages/cli/src/agents/` — 注入 EventBus 到 Agent tools
- `packages/cli/src/services/` — 新增 AgentRunnerService
- `packages/cli/src/tools/` — advance_stage、add_comment 注入 EventBus
- `packages/cli/package.json` — 新增前端构建依赖
- 新增 `packages/cli/web/` — 前端源码目录（React SPA）
- 构建流程 — 新增 Vite 构建步骤，产物嵌入 server
