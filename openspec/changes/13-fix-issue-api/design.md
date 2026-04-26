## Context

后端 `AgentRunnerService` 已有完整的并行基础设施：`activeAgents: Map<string, RunningAgent>`、`maxConcurrentAgents` 配置（默认 8，范围 1-16）、`getStatus()` 返回 `activeAgents` 数组。但三层撕裂使得这些设施失效：

1. **后端引擎** — `startPipeline()` 检查 `activeAgents.has(issue.id)` 但从不检查 `activeAgents.size >= maxConcurrentAgents`
2. **后端 API 类型** — `AgentStatus` 接口已包含 `activeAgents` 数组和 `queueDepth`，`getStatus()` 已返回完整数据
3. **前端类型** — `types.ts` 的 `AgentStatus` 只有 `{ running, issueId, issueNumber }`，不包含 `activeAgents`
4. **前端 UI** — `IssueDetailPage` 用 `agentStatus.running` 全局布尔锁禁用所有 Start/Approve 按钮；`IssueCard` 比较单个 `issueId`

关键约束：后端 `getStatus()` 已返回 `activeAgents` 和所有并行信息，无需修改后端返回结构，只需前端类型匹配 + 后端加入并发检查。

## Goals / Non-Goals

**Goals:**
- `startPipeline()` 执行 `maxConcurrentAgents` 上限检查
- 前端 `AgentStatus` 类型匹配后端返回结构
- Start/Approve 按钮按 per-issue 粒度判断
- IssueCard 按 per-issue 显示 running 指示器

**Non-Goals:**
- 不实现排队机制（spec 已定义但当前无队列基础设施，本次只做上限拒绝）
- 不修改后端 `getStatus()` 返回结构（已包含所需字段）
- 不修改 SSE 事件结构

## Decisions

### D1: 并发超限返回 429 + 在 API 层和 startPipeline 双重检查

`startPipeline()` 返回 `{ started: false, error }` 是已有的模式。API 路由层（`issues.ts`）已有前置检查（blocked、closed、paused、非 draft、isRunning），在此基础上增加并发上限检查。两层都检查：
- `startPipeline()` 中：在 `activeAgents.has(issue.id)` 之后加 `activeAgents.size >= maxConcurrentAgents`，返回 `{ started: false, error: "Concurrent agent limit reached (N)" }`
- API 路由层：检查 `agentRunner.getStatus().activeAgents.length >= agentRunner.getMaxConcurrentAgents()`，超限返回 429

API 层检查让 HTTP 语义清晰（429 Too Many Requests），`startPipeline` 层检查保护服务不被绕过 API 直接调用。

**Alternatives considered:**
- 只在 `startPipeline` 中检查，API 层透传错误码 → 无法区分 409（重复）和 429（超限），前端不好展示
- 不在 `startPipeline` 中检查，只在 API 层 → 如果将来有其他入口（CLI、其他 API），会绕过检查

### D2: 前端类型扩展而非替换

向后兼容：保留 `running`、`issueId`、`issueNumber` 旧字段（后端仍返回），新增 `activeAgents` 数组和 `maxConcurrentAgents`。组件逐步迁移到新字段，旧字段不做 breaking change。

```typescript
export interface AgentStatus {
  running: boolean
  issueId: string | null
  issueNumber: number | null
  activeAgents: Array<{ issueId: string; issueNumber: number; projectId: string }>
  maxConcurrentAgents: number
  queueDepth: number
  waitingQuestions: Array<{ issueId: string; issueNumber: number; projectId: string; questionId: string; question: string }>
  recoverableIssues: Array<{ issueNumber: number; stage: string }>
}
```

### D3: IssueCard 的 per-issue 判断通过 helper 函数

从 `activeAgents` 数组中查找该 issue 是否在跑：`activeAgents.some(a => a.issueNumber === issue.number)`。这样 `IssueCard` 不再依赖全局 `running` 布尔值。`KanbanBoard` → `StageColumn` → `IssueCard` 传递链不变，只是类型从单 agent 扩展为多 agent。

### D4: `getStatus()` 增加 `maxConcurrentAgents` 字段

后端 `getStatus()` 当前返回 `queueDepth` 但不返回 `maxConcurrentAgents`。在返回对象中加一个 `maxConcurrentAgents: this.maxConcurrentAgents`，使前端能展示上限和容量。

## Risks / Trade-offs

- **[风险] 并发超限时无排队，用户需手动重试** → 前端按钮 disabled 状态显示 "Capacity full..."，用户知道要等；后续可加队列
- **[风险] `getStatus()` 增加 `maxConcurrentAgents` 字段是后端改动** → 改动极小（加一个字段），无 breaking change，旧前端忽略新字段
- **[风险] IssueDetailPage 的 SSE streaming 状态依赖 `isAgentRunningOnThis`** → 改为从 `activeAgents` 判断，逻辑等价但数据源变了，需确认 SSE 事件关联不受影响

## Migration Plan

1. 后端：`agent-runner-service.ts` — `startPipeline()` 加并发检查 + `getStatus()` 加 `maxConcurrentAgents` 字段
2. 后端：`issues.ts` API — start handler 加 429 返回
3. 前端：`types.ts` — 扩展 `AgentStatus` 类型
4. 前端：`IssueDetailPage.tsx` — 重写 Start/Approve 按钮逻辑
5. 前端：`IssueCard.tsx` — 改用 `activeAgents` 判断 running
6. 无数据库迁移，无配置迁移

## Open Questions

_无_
