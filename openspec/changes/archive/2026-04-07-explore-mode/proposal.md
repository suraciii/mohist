## Why

mohist 当前只有 Pipeline 模式——用户创建 Issue、启动 Agent、Agent 自主执行 plan→build→check。但在启动 Pipeline 之前，用户需要先想清楚"做什么"和"怎么做"。这个需求探索阶段没有工具支持：用户要么自己在代码库里翻找然后手写 Issue 描述，要么在脑中想好再输入给 mohist。Explore 模式提供一个 AI 驱动的对话界面，让用户和 Agent 一起探索需求、理解代码库、澄清模糊点，最终将探索成果结晶为 Issue 进入 Pipeline。

## What Changes

- 新增 Explore Session 数据模型（explore_sessions + explore_messages 表）
- 新增 Explore Agent：独立的 system prompt（思考伙伴角色）和工具集（read_file、glob、grep——全部只读）
- 新增 `create_issue` 桥接工具：探索完成后将对话精华提炼为结构化 Issue 描述
- 新增 Explore API：session CRUD + 消息发送（streaming response via SSE）
- 新增 Explore 聊天前端页面：`/explore/:id` 路由，支持 markdown 渲染、tool call 折叠/展开、流式输出
- Header 增加 Explore 入口按钮

## Capabilities

### New Capabilities
- `explore-session`: Explore 会话的生命周期管理（创建、持久化消息历史、crystallize 为 Issue）
- `explore-agent`: Explore Agent 的 system prompt、工具集（read_file/glob/grep/create_issue）、与 Main Agent 的工具隔离
- `explore-api`: Explore REST API（session CRUD、消息发送、SSE streaming response）
- `explore-web-ui`: Explore 聊天前端界面（消息列表、tool call 折叠、流式输出、create issue 动作）

### Modified Capabilities
- `http-api`: 新增 `/api/explore` 路由组
- `web-ui`: Header 增加 Explore 入口，新增 `/explore/:id` 路由

## Impact

- `packages/cli/src/db/` — 新增 explore-session-repo.ts、explore-message-repo.ts
- `packages/cli/src/agents/` — 新增 explore-agent.ts
- `packages/cli/src/tools/` — 新增 read-file.ts、glob-tool.ts、grep-tool.ts、create-issue-tool.ts
- `packages/cli/src/services/` — 新增 explore-service.ts
- `packages/cli/src/api/` — 新增 explore.ts
- `packages/cli/src/server/index.ts` — 注册新路由
- `packages/cli/web/src/` — 新增 Explore 聊天页面组件
- 数据库迁移 — 新增 explore_sessions、explore_messages 两张表
