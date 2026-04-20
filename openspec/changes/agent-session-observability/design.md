## Context

当前 agent session 的可观测性完全断裂。深入分析 opencode 会话架构（`opensrc/opencode/`）后，发现了三个致命 bug、一个架构缺失、和三个前端问题。

### 致命 Bug

**Bug 1: `eventBus` 从未传递给 `acpOptions`**

`api/issues.ts` 构造 `acpOptions` 时未包含 `eventBus`（共 5 处，非原先以为的 4 处——`/reopen` 路由被遗漏）。`acp-session.ts` 中 `if (eventBus && executionId)` guard 永远为 false。**Pipeline 运行期间零 SSE 事件到达前端。**

**Bug 2: `issueId` 类型不匹配 + 双重用途**

后端 emit UUID (`issue.id`)，前端 `useAgentSession.ts` 对比 `String(issueNumber)`（数字如 `"1"`）。即使事件到达也永远不会通过过滤。

此外，`issueId` 字段同时被用于：(a) DB 外键关联（需要 UUID），(b) SSE 事件标识（前端期望数字）。不能简单地把一个改成另一个——需要分离。

**Bug 3: Plan 阶段没有 `executionId`**

Plan 阶段的 `acpOptions` 不设 `executionId`（它与 coder session 绑定）。即使修好 Bug 1，guard 仍然失败。

### 架构缺失

**缺失 1: `createAcpConnection` 没有暴露 sessionUpdate 的机制**

`AcpConnection` 接口只有 `{ prompt(), close() }`。sessionUpdate handler 绑定在 `ClientSideConnection` 构造函数内部（acp-session.ts 第 446-548 行），外部调用方无法拦截。如果直接修好 Bug 1 + 3，Plan 阶段会自动产生 `coder_text_chunk` / `coder_tool_call` 事件——但前端需要的是带 round 语义的 `plan_session_update` 事件，不是无 round 标签的 `coder_*` 事件。如果两者都 emit，会产生语义重复。

**缺失 2: Build 阶段（RalphExecutor）零可观测性**

`workflow-controller.ts` 的 `runPipelineBuildStage`（第 269-278 行）构造 `RalphExecutor` 时不传 `eventBus`：
```typescript
const executor = new RalphExecutor({
  worktreePath: this.worktreePath,
  projectPath: this.worktreePath,
  issueId: issue.id,
  projectId: issue.projectId,
  // 没有 eventBus, workflowLogRepo, coderSessionRepo
});
```
但 `RalphExecutor` 内部已经有 eventBus 支持（第 276、291、343 行），只是没被传入。后果：Build 阶段零 SSE 事件、零 workflow_log 记录、零 coder_session 记录。

### 前端问题

**问题 1: `agentStatus.issueId === String(issueNumber)` 始终 false**

`AgentRunnerService.getStatus()` 返回 `issueId` 是 UUID，前端用 `String(issueNumber)` 比较。"当前 agent 是否在此 issue 上运行" 的检测永远失败，导致 `isStreaming` 永远不会变成 `true`，新 run 开始时的状态重置也永远不触发。

**问题 2: `useAgentSession` 不订阅新事件类型**

Hook 订阅 `agent_text_chunk` / `main_tool_call` / `coder_text_chunk` / `coder_tool_call`，但提案新增的 `plan_session_update` / `plan_round_start` 不在订阅列表中。

**问题 3: 前端 types 不完整**

`AgentDetailEventMap` 和 `ToolCallEntry` 需要新字段/类型（plan 事件类型、rawInput/rawOutput/title）。

### opencode 参考

opencode（`opensrc/opencode/`）使用 Message+Part 两层模型 + SyncEvent 事件溯源 + Projector 读模型投影。关键借鉴模式：

| 模式 | opencode | mohist 适配 |
|------|----------|------------|
| Part 抽象 | ToolPart { state: pending→running→completed, input, output, title } | ✓ 用于前端渲染单元 |
| 双路径流式 | PartDelta（短暂实时）+ PartUpdated（持久查询） | ✓ ACP→SSE（实时）+ workflow_log→查询（持久） |
| 事件投影器 | SyncEvent→Projector→读模型表 | ○ 当前不需要——workflow_log 已够用 |

## Goals / Non-Goals

**Goals:**
- 修复三个致命 bug + `/reopen` 遗漏，让 SSE 事件管道跑通
- Plan 阶段 ACP 事件实时推送到前端，携带 round 元数据
- Review 阶段同享 Plan 的事件桥接机制（后端统一处理）
- Build 阶段（RalphExecutor）的 SSE 事件和 workflow_log 记录正常工作
- Coder session 的 tool call 事件携带完整的 args 和 result
- 前端按 round 分组展示完整对话链
- 前端正确检测 agent 运行状态

**Non-Goals:**
- 事件溯源 / CQRS 架构（workflow_log 已是 append-only event store，无需额外 EventTable）
- agent_thought_chunk 的展示（暂时只在 DB 中存储）
- 分页或懒加载（当前单 issue 数据量可接受）
- Explore 阶段的 session 展示（走独立 ExplorePage）
- Review 阶段的前端 UI（后端事件桥接先行，前端后跟）

## Decisions

### D1: `AcpSessionOptions` / `AcpConnectionOptions` 扩展三个新字段

在两个 option 接口中增加：
```typescript
issueNumber?: number;       // 用于 SSE 事件的 issueId（前端匹配用）
executionId?: string;       // 已存在，Plan 阶段需设置
onSessionUpdate?: (notification: SessionNotification) => void;  // Plan/Review 阶段事件桥接
```

**理由**: 三个问题（issueId 双重用途、sessionUpdate 拦截、executionId 缺失）都在 option 层面解决最干净。调用方按需传入，不改底层行为。

### D2: `issueId` 双轨制——UUID 用于 DB，number 用于 SSE

`acp-session.ts` 中：
- `workflowLogRepo.insert(issueId, ...)` → 继续用 UUID（`options.issueId`）
- `coderSessionRepo.insert({ issueId, ... })` → 继续用 UUID
- `eventBus.emit('coder_text_chunk', { issueId: String(options.issueNumber ?? options.issueId) })` → 优先用 number

**理由**: 最小改动。DB 层和 SSE 层各用各的 ID，互不干扰。新增 `issueNumber` 字段而非修改 `issueId` 的语义。

**风险**: `issueNumber` 为 undefined 时 fallback 到 UUID（兼容 Explore 等未传 issueNumber 的场景），前端匹配可能失败——但 Explore 不在 scope 内。

### D3: `onSessionUpdate` 回调存在时，`createAcpConnection` 跳过内部 `coder_*` emit

当 `options.onSessionUpdate` 被设置时，`createAcpConnection` 内部的 sessionUpdate handler：
- 仍然执行：agentText 累积、`workflowLogRepo.insert()`
- 跳过：`coder_text_chunk` 和 `coder_tool_call` emit
- 调用：`options.onSessionUpdate(notification)` 交给外部处理

**理由**: 避免语义重复。Plan/Review 阶段通过 `onSessionUpdate` 产生 `plan_session_update` 事件（有 round 元数据），不需要同时产生无 round 标签的 `coder_*` 事件。Build 阶段不设 `onSessionUpdate`，保持原有 `coder_*` 行为。

```
                    onSessionUpdate 存在?       
                    ┌──── YES ────┐  ┌──── NO ────┐
                    │              │  │             │
  agentText 累积    │     ✓       │  │     ✓       │
  workflowLog insert│     ✓       │  │     ✓       │
  coder_* emit      │     ✗       │  │     ✓       │
  onSessionUpdate() │     ✓       │  │     ✗       │
                    │  Plan/Review│  │   Build     │
                    └─────────────┘  └─────────────┘
```

### D4: `executionId` 按阶段区分

`WorkflowController.run()` 在 dispatch 到不同阶段时，按需覆盖 acpOptions 的 executionId：

| 阶段 | executionId | 来源 |
|------|------------|------|
| Plan | `plan-${issue.number}` | runPlanStage 内部覆盖 |
| Build | `build-${issue.number}-${taskId}` | RalphExecutor 逐 task 设置 |
| Review | `review-${issue.number}` | runPipelineReviewStage 内部覆盖 |

**理由**: acpOptions 在路由层一次性构造，但 executionId 应该反映当前执行上下文。前端可通过 executionId 前缀区分事件来源。

**实现**: `runPlanStage` 和 `runPipelineReviewStage` 在调用 `createAcpConnection` 前 clone acpOptions 并设置 executionId。`RalphExecutor` 的 `emitTaskUpdate` 已使用 `context.executionId`，只需在构造时传入即可。

### D5: Build 阶段纳入本次变更

**选择**: 扩展 `RalphExecutorContext` 和 `runPipelineBuildStage`，让 Build 阶段正常产生 SSE 事件和 workflow_log 记录。

**理由**: 后端改动很小——`RalphExecutor` 已有 eventBus 支持（只是没被传入），只需：
1. `runPipelineBuildStage` 构造 `RalphExecutor` 时传入 `this.eventBus`
2. `RalphExecutorContext` 增加 `workflowLogRepo`、`coderSessionRepo`、`issueNumber` 字段
3. `_acpSessionRunner` 调用时传入新增字段

前端已有 `CoderSubTimeline` 组件处理 coder session 展示（`AgentSessionPanel` 的子组件），数据通路打通后自动生效。

**不做的事**: Build 阶段的 Round 语义（每个 task 是一个 round）——这是前端 SessionTimeline 的事，后端只需要让事件到达前端。

### D6: 前端 `agentStatus` 改用 `issueNumber` 字段

**选择**: `useAgentSession.ts` 中把 `agentStatus.issueId === String(issueNumber)` 改为 `agentStatus.issueNumber === issueNumber`。

**理由**: `AgentRunnerService.getStatus()` 同时返回 `issueId`（UUID）和 `issueNumber`（数字）。直接比较 number 字段即可，无需改后端。

### D7: 所有 5 处 `acpOptions` 构造点补上 `eventBus` + `issueNumber`

| 路由 | 行号 | 补充内容 |
|------|------|---------|
| `POST /:number/start` | 343 | eventBus, issueNumber |
| `POST /:number/reopen` | 491 | eventBus, issueNumber |
| `POST /:number/approve` | 729 | eventBus, issueNumber |
| `POST /:number/reject` | 846 | eventBus, issueNumber |
| `POST /:number/messages` | 936 | eventBus, issueNumber |

### D8: 前端历史数据统一从 `workflow_log` API 加载

（与原方案相同，不赘述）

### D9: `coder_tool_call` 事件 payload 增加 `rawInput`、`rawOutput`、`title`

（与原方案相同，不赘述）

### D10: SSE 事件格式用独立 event name

（与原方案相同，不赘述）

### D11: Build 阶段 executionId 按 task 区分

**选择**: `runPipelineBuildStage` 传入 `executionId: 'build-${issue.number}'`，RalphExecutor 内部为每个 task 生成唯一 `taskExecutionId = '${context.executionId}-${taskId}'`（如 `"build-1-T-001"`）。

**理由**:
1. 当前 RalphExecutor 对所有 task 使用同一个 `context.executionId`，导致前端无法区分不同 task 的 coder session
2. `ralph_task_update` 和 `coder_text_chunk` / `coder_tool_call` 需要 task 级粒度
3. `ralph_loop_progress` 保持使用 `context.executionId`（loop 级别，不需要 task 粒度）

**实现**:
- `runPipelineBuildStage` 构造 RalphExecutor 时设置 `executionId: 'build-${issue.number}'`
- RalphExecutor 的 task loop 中：`const taskExecutionId = '${context.executionId}-${nextTask.id}'`
- `_acpSessionRunner` 调用使用 `taskExecutionId`
- `emitTaskUpdate` 使用 `taskExecutionId`
- `emitLoopProgress` 继续使用 `context.executionId`

### D12: 前端去重策略简化

**选择**: 抛弃 "timestamp proximity + content overlap" 的复杂方案，采用简化的去重策略：
- **Tool calls**: 使用 `Map<toolCallId, ToolCallEntry>` 去重（必须，因为同一个 toolCall 有 started + completed 两条记录）
- **Text chunks**: 无需去重（增量累加，每个 chunk 内容不同）
- **Rounds**: 通过 `roundIndex` 区分
- **跨 run**: 检测 `plan_round_start` 的 `roundIndex === 0` 表示新 run 开始

**理由**:
1. 历史数据（workflow_log）和实时数据（SSE）天然按时间分割，重叠概率极低
2. Text chunks 是增量累加的，历史数据包含完整文本，实时数据是增量，不会重复
3. Tool call 去重是必要的，因为 workflow_log 中同一个 toolCall 有 started 和 completed 两条记录
4. 简化的策略降低实现复杂度，减少 bug 风险

### D13: onSessionUpdate 回调使用显式 state 对象

**选择**: `runPlanStage` 和 `runPipelineReviewStage` 使用显式 `roundState` 对象替代 let + 外部变量的隐式闭包：

```typescript
const roundState = { type: '', index: 0 };

const conn = await createAcpConnection({
  ...acpOptions,
  onSessionUpdate: (notification) => {
    eventBus.emit('plan_session_update', {
      roundType: roundState.type,
      roundIndex: roundState.index,
      // ...
    });
  },
});

for (const [index, round] of rounds.entries()) {
  roundState.type = round.type;
  roundState.index = index;
  // ...
}
```

**理由**:
1. 显式 state 对象比隐式闭包更易读，维护者一眼就能看出变量的用途
2. JavaScript 单线程模型保证闭包安全，但需要注释说明以避免维护者疑虑
3. 代码中添加注释：`// roundState is mutated by the for loop and read by onSessionUpdate callback. Safe because JS is single-threaded.`

## Risks / Trade-offs

- **[onSessionUpdate 回调同步性]** `onSessionUpdate` 在 ACP sessionUpdate handler 内部同步调用。如果回调中有耗时操作会阻塞 ACP 消息处理。→ **缓解**: 回调应只做 eventBus.emit（同步、快速），不做 DB 查询或其他 I/O。

- **[EventBus 事件量增加]** Plan 阶段每轮 200-400 个 sessionUpdate，5 轮约 1000-2000 个。→ **缓解**: 前端已有 ref-based buffer + requestAnimationFrame 批量更新模式。

- **[Build 阶段 issueId 类型]** RalphExecutor 的 `emitTaskUpdate` 用 `context.issueId`（UUID）作为 SSE issueId。→ **缓解**: 同时传入 `issueNumber`，emit 时优先用 number。与 acp-session.ts 同一策略。

- **[workflow-controller 改动风险]** Plan 阶段是 pipeline 核心路径。→ **缓解**: 桥接逻辑是 fire-and-forget，onSessionUpdate 异常不传播。
