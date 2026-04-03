# Web UI 探索

> 日期: 2026-04-03
> 参与者: surac, opencode (explore mode)

## 背景

为 mohist 设计 Web UI，让用户能通过浏览器查看项目、Issues、Workflow 进度和进行中的任务。

## 现状分析

### 后端 API 层

当前 mohist 使用 Express + SQLite，已提供完整的 REST API：

- `/api/projects` — 项目 CRUD
- `/api/issues` — Issue CRUD + workflow 控制 (start/close/reopen)
- `/api/labels` — 标签列表
- `/api/config` — 配置读写
- `/api/health` / `/api/status` — 状态检查

**缺失**: 无前端、无静态文件服务、无 CORS、无认证、无实时推送。

### Agent 输出存储

关键发现——Agent 的实时输出几乎不持久化：

| 数据 | 存储位置 | 持久性 |
|------|----------|--------|
| Issue stage 变化 | SQLite `issues` 表 | 持久 |
| Agent 写的评论 | SQLite `comments` 表 | 持久 |
| LLM 原始消息 | `SessionManager` 内存 Map | 重启丢失 |
| Code Agent 输出 | `spawn_coder` 局部变量 | 执行完丢失 |
| Agent 日志文件 | CLI 期望存在但无代码写入 | 不存在 |

### 已有但被浪费的流式管道

1. **Vercel AI SDK streamText()** — 支持 token 级增量输出，但代码里 `consumeStream()` 立即全量消费
2. **ACP sessionUpdate** — Code Agent 的增量输出在流式回来，但只累积在局部变量

## 参考: opencode 的 Web UI 架构

### 三套 UI 共享后端

- **TUI** — @opentui/solid (SolidJS 渲染到终端)
- **Web UI** — SolidJS + Vite + Tailwind CSS 4 + TanStack Query
- **Desktop** — Tauri/Electron 包裹同一个 Web UI

### 核心模式

1. **嵌入式 UI** — Vite build 产物嵌入 server 二进制，`opencode serve` 一条命令提供 API + UI
2. **SSE 实时推送** — 三层事件架构: Effect PubSub → SSE endpoint → 客户端 (事件合并 + 帧批处理)
3. **目录级多项目** — 单 server 实例通过 `x-opencode-directory` header 路由多项目
4. **OpenAPI-First** — hono-openapi 自动生成 spec，SDK 从 spec 自动生成
5. **TUI-Web Bridge** — `/tui/*` 路由允许 Web UI 控制 TUI

### 技术栈

Hono (HTTP), SolidJS (前端), Tailwind CSS 4 (样式), TanStack Query (数据获取), Effect v4 (业务逻辑), Drizzle ORM (数据库), SST + Cloudflare (部署)

## 决策记录

### 1. HTTP 框架: Express → Hono

**决策**: 换 Hono

理由:
- 内置 `streamSSE`，SSE 实现更优雅
- 更好的 TypeScript 类型安全
- 更好的性能
- opencode 验证过的选择
- 迁移成本约 1-2 小时，业务层 (services/db/agent) 不受影响

### 2. UI 部署方式: 嵌入式

**决策**: 嵌入式 UI (参考 opencode)

理由:
- `mo server` 一条命令同时提供 API + UI，零部署复杂度
- 无 CORS 问题 (同源)
- 对开发者工具来说用户体验最佳
- 开发时 Vite dev server proxy 到后端，生产时 build 产物嵌入 server

### 3. 实时推送: SSE

**决策**: SSE (Server-Sent Events)

理由:
- mohist 的场景主要是"看进度"，单向推送足够
- 比 WebSocket 简单，不需要双向通信
- Hono 内置 streamSSE 支持

### 4. 实时进度分阶段实现

**决策**: 分两阶段

**Level 1 (MVP)**: 状态级事件
- `stage_changed`, `comment_added`, `agent_done`, `error`
- 数据已在 SQLite 中，只需在 repo 方法里包裹 Bus emit
- 不侵入 agent 核心逻辑

**Level 2 (后续)**: 动作级事件
- `tool_call`, `tool_result`, `agent_message_chunk`
- 需要接通 Vercel AI SDK stream 和 ACP sessionUpdate
- 让用户看到 agent 在执行什么工具

### 5. 前端框架: 待定

SolidJS (opencode 同款) vs React (生态大)，UI 相对简单（看板+列表+详情），两者都够用。后续决定。

## Web UI 功能设想

### MVP (Level 1 事件)

- 看板视图 — Issue 按 stage (draft/plan/build/check/done) 分列
- Issue 详情 — 描述、评论、状态
- 项目切换 — 多项目之间切换
- 实时更新 — stage 变化、评论添加时自动刷新

### 后续 (Level 2 事件 + 操作能力)

- Agent 进度展示 — 工具调用列表
- 操作能力 — 创建 Issue、启动 Agent、审批 gate、关闭/重开
- 配置管理 — 模型、超时等

## Event Bus 设计思路

```
IssueRepo.updateStage(issueId, 'plan')
  → 写 SQLite
  → bus.emit('stage_changed', { issueId, from, to })
  → SSE 推给浏览器
```

Bus 包裹现有数据库操作，不侵入 agent 核心逻辑。Level 1 的事件数据源全部已在 SQLite 中。

## 待定事项

- [ ] 前端框架选型 (SolidJS vs React)
- [ ] 是否需要认证
- [ ] 嵌入式 UI 的具体构建流程设计
