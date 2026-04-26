## Why

后端引擎 `AgentRunnerService` 已支持多 issue 并行（`activeAgents: Map`），但三层撕裂导致实际只能串行：前端用全局布尔锁禁用所有 Start 按钮、API 类型只暴露单 agent 字段、`startPipeline()` 从不检查并发上限。这使得已有的 `maxConcurrentAgents` 配置和 `activeAgents` 追踪机制完全失效。

## What Changes

- **后端并发上限执行** — `startPipeline()` 中加入 `activeAgents.size >= maxConcurrentAgents` 检查，超限时返回 429 错误（非排队，因为 spec 已定义排队语义但当前实现无队列，本次只做上限拒绝）
- **API 类型扩展** — 前端 `AgentStatus` 类型加入 `activeAgents` 数组（每个含 `issueId`, `issueNumber`）和 `maxConcurrentAgents` 数值字段，匹配后端 `getStatus()` 已有的返回结构
- **前端按 issue 粒度判断** — Start 按钮禁用逻辑从「全局有 agent 在跑」改为「该 issue 已在跑 OR 达到并发上限」
- **前端 running 指示器** — IssueCard 和 KanbanBoard 根据 `activeAgents` 数组标记具体哪个 issue 在跑，而非全局状态

## Capabilities

### New Capabilities

_无_

### Modified Capabilities

- **agent-pool** — `startPipeline()` 需实际执行并发上限检查（spec 已定义 `activeAgents.size >= maxConcurrentAgents` 场景，但实现未落地）
- **http-api** — `GET /api/agent/status` 返回值的类型定义需在前端匹配（`activeAgents` 数组、`maxConcurrentAgents` 字段）
- **web-ui** — Start 按钮禁用逻辑从全局锁改为按 issue 粒度 + 并发上限判断；IssueCard running 指示器从全局改为 per-issue
- **agent-session-ui** — `agentStatus` 检测逻辑需适配多 agent 场景（从 `running: boolean` 改为 `activeAgents: Array`）

## Impact

- `packages/cli/src/services/agent-runner-service.ts` — `startPipeline()` 增加并发上限检查
- `packages/cli/web/src/lib/types.ts` — `AgentStatus` 类型扩展
- `packages/cli/web/src/components/IssueDetailPage.tsx` — Start 按钮判断逻辑重写
- `packages/cli/web/src/components/IssueCard.tsx` — running 指示器改用 per-issue 判断
- `packages/cli/web/src/components/KanbanBoard.tsx` — 传递 `activeAgents` 信息到子组件
