## Context

`spawn_coder.ts` 通过 ACP SDK 与 `opencode acp` 子进程通信。当前 `sessionUpdate` handler 只处理 `agent_message_chunk`，拼接为 `agentText` 字符串，最后通过 `maybeTruncate()` 截断为 8000 字符返回给 Main Agent。

ACP SDK 提供的 `SessionUpdate` 联合类型包含 11 种事件，其中 `tool_call`（含文件路径、diff、状态）和 `tool_call_update`（进度、输出）对调试和实时展示最有价值。`usage_update`（token 消耗）和 `stopReason`（结束原因）对审计重要。

当前没有任何持久化的执行日志——所有运行记录随 server 重启丢失。

## Goals / Non-Goals

**Goals:**

- 捕获 spawn_coder 过程中的所有 ACP 事件
- 持久化到 workflow_log 表，支持事后查询和审计
- 通过 EventBus 推送 action-level 事件（tool_call），为 mo attach 和 Web UI 提供数据源
- 保留现有行为：返回给 Main Agent 的仍是截断摘要文本

**Non-Goals:**

- 不改变 Main Agent 收到的 spawn_coder 返回值格式
- 不实现 Web UI 的实时日志视图（留给后续 change）
- 不修改 workflow_log 的查询 API（只做基础 CRUD）
- 不做 ACP 事件的实时 SSE 推送（频率太高，留给 mo attach change 评估）
- 不解决输出截断问题（B-322，留给后续 change）

## Decisions

### D1: workflow_log 表使用 JSON data 列

```sql
CREATE TABLE workflow_log (
  id TEXT PRIMARY KEY,
  issue_id TEXT NOT NULL REFERENCES issues(id) ON DELETE CASCADE,
  session_id TEXT,
  event_type TEXT NOT NULL,
  data TEXT NOT NULL DEFAULT '{}',
  created_at TEXT NOT NULL DEFAULT (datetime('now'))
);
CREATE INDEX idx_workflow_log_issue ON workflow_log(issue_id, created_at);
```

`data` 列存完整 JSON payload，不同事件类型结构不同。用 SQLite 的 `json_extract()` 按需查询。

**替代方案**：为每种事件类型建独立列（tool_name, file_path, diff 等）。不采用——事件类型多且结构各异，会导致大量 nullable 列。

### D2: spawn_coder 接收 WorkflowLogRepo 和 issueId 作为参数

```
AgentRunnerService.start()
  └→ runMainAgent({ issue, ..., workflowLogRepo })
        └→ createSpawnCoderTool({ worktreePath, issueId: issue.id, workflowLogRepo })
              └→ runAcpOneshot(cwd, task, timeout, issueId, workflowLogRepo)
                    └→ sessionUpdate handler → workflow_log(issue_id)
```

`createSpawnCoderTool` 签名：
```typescript
interface SpawnCoderContext {
  worktreePath?: string;
  issueId: string;           // 用于关联 workflow_log 记录
  workflowLogRepo?: WorkflowLogRepo;  // 写入事件
  eventBus?: EventBus;       // 推送 tool_call 事件
}
```

`runAcpOneshot` 签名：
```typescript
async function runAcpOneshot(
  cwd: string,
  task: string,
  timeout: number,
  issueId: string,
  workflowLogRepo?: WorkflowLogRepo,
  eventBus?: EventBus,
): Promise<{ text: string; error?: string }>
```

**issueId 传递路径**：
- `AgentRunnerService.start()` 有 `issue: Issue` 参数，从中取出 `issue.id`
- 通过 `MainAgentContext.issue.id` 传给 `createSpawnCoderTool`
- 工具执行时通过 `params` context 传给 `runAcpOneshot`

**替代方案**：在 AgentRunnerService 层面包装 spawn_coder，service 层负责日志记录。不采用——sessionUpdate 事件在 spawn_coder 内部产生，外包会增加复杂度，且会丢失事件时序。

### D3: EventBus 只推送低频 action 事件

新增 EventBus 事件类型：
- `tool_call`: 当 ACP 报告 tool_call 事件时 emit（包含 tool name、file、status）
- 不推送 `agent_message_chunk`（频率太高，不适合 SSE）

EventBus 推送用于 Web UI 的状态更新（如 "agent is editing foo.ts"），完整事件记录在 workflow_log 表中。

### D4: 返回给 Main Agent 的文本不变

`spawn_coder` 的 execute 仍然返回截断后的 `agentText` 字符串。完整文本存在 workflow_log 中。

这意味着 Main Agent 看到的和之前一样，但用户/开发者可以通过 API 或 CLI 查看完整执行日志。

### D5: workflow_log 记录粒度

只记录 spawn_coder（子 agent）的 ACP 事件，不记录 Main Agent 的 LLM loop 事件（tool calls、decisions）。

理由：Main Agent 的 loop 事件（read_workflow、advance_stage 等）已经通过 EventBus 的事件和 comments 记录了。需要额外记录的是子 agent 的执行细节，这些目前完全丢失。

### D6: sessionUpdate 错误处理策略

每个 sessionUpdate 事件处理器用 try/catch 包裹，单个事件的解析错误不影响其他事件处理，也不中断整个 oneshot 流程。

```typescript
sessionUpdate: async (notification: SessionNotification) => {
  try {
    const update = notification.update;
    // 处理各种事件类型...
  } catch (err) {
    console.error('[spawn_coder] sessionUpdate error:', err);
    // 不中断，继续处理下一个事件
  }
}
```

**workflow_log 写入失败策略**：
- 写入失败 → `console.error` 记录 → **继续执行**（不影响 agent 正常返回）
- EventBus emit 失败 → silent swallow（已有策略）

### D7: EventBus 修改与 ask-user change 的协调

`acp-event-capture T-004` 和 `ask-user T-003` 都修改 `services/event-bus.ts` 的 `EventMap` 和 `api/events.ts` 的 `ALL_EVENT_TYPES`。

**执行顺序约定**：
1. 先完成 `acp-event-capture T-004`（添加 `tool_call`）
2. 再完成 `ask-user T-003`（添加 `question_asked`, `question_answered`）

两个 change 不同时修改同一文件。实现前需确认对方未占用。

### D8: WorkflowLogRepo 方法集

根据 spec/workflow-log/spec.md，WorkflowLogRepo 需要以下方法：

```typescript
class WorkflowLogRepo {
  insert(issueId: string, sessionId: string | null, eventType: string, data: object): WorkflowLogEntry
  findByIssueId(issueId: string, eventType?: string): WorkflowLogEntry[]
  findById(id: string): WorkflowLogEntry | null
  findBySessionId(sessionId: string): WorkflowLogEntry[]  // 用于关联同一次 spawn 的事件
}
```

**排序**：所有查询结果按 `created_at ASC` 排列（时序一致）。

## Risks / Trade-offs

- **[Risk] workflow_log 数据量增长快** → 每次 spawn_coder 可能产生几十到几百条事件。对于一个 issue 的完整 plan→build→check 流程，估计 100-500 条记录。SQLite 处理这个量级没有问题。可考虑后续添加清理策略。
- **[Risk] JSON data 列查询效率** → SQLite 的 `json_extract()` 有性能开销，但通过 `(issue_id, created_at)` 复合索引可以高效过滤。查询场景主要是 "查看某个 issue 的所有日志"，这个模式很适合索引。
- **[Low] 不修改现有行为** → 只增加写入，不改变读取路径，回归风险极低
- **[Risk] EventBus 类型冲突** → `ask-user` change 也修改 EventMap 和 ALL_EVENT_TYPES。需约定执行顺序，避免同时修改同一文件。
- **[Risk] issueId 传递断裂** → 如果任何一环遗漏 issueId，workflow_log 记录会缺失 issue_id。通过 D2 的完整传递路径确保每个环节都传递。
