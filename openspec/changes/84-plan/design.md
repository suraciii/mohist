## Context

Plan 阶段执行时，后端已有 `emitProgress()` 方法（`workflow-controller.ts:95`）和 `plan_round_start` SSE 事件，但两者都未被充分利用：

- `emitProgress()` 在 `runPlanStage()` 中**从未被调用**，导致 `/api/agent/status` 返回的 `AgentProgress.roundType` / `roundIndex` / `taskProgress` 在 plan 阶段始终为 `undefined`
- `plan_round_start` 已存在但缺少对应的 `plan_round_complete` 事件，前端无法得知步骤何时结束
- 前端 `useSessionTimeline` 只跟踪 `rounds[]`（文本流）和 build 阶段的 `taskProgress`/`loopProgress`，plan 阶段没有进度状态
- `SessionTimeline` 的 `TaskProgressPanel` 仅在 `currentStage === 'build'` 时渲染，plan 阶段无对等面板

Build 阶段的进度模式已经验证可行：`RalphExecutor` → `ralph_task_update` / `ralph_loop_progress` EventBus 事件 → SSE → `useSessionTimeline.taskProgress` → `TaskProgressPanel`。Plan 阶段应复用相同的事件流架构。

## Goals / Non-Goals

**Goals:**
- 后端：每个 artifact round 完成后调用 `emitProgress()` 并发射 `plan_round_complete` SSE 事件
- 前端：新增 `PlanProgressPanel` 组件，展示 5 个基础步骤 + auto-fix 扩展步骤的状态
- 支持页面刷新后从 API 恢复进度、checkpoint 恢复后立即显示已完成步骤
- 步骤完成后显示耗时、self-review 显示 PASS/FAIL verdict

**Non-Goals:**
- 不修改现有的 `plan_session_update` / `plan_round_start` 机制
- 不做步骤取消/重试功能（只展示，不交互）
- 不做 Plan 阶段的可视化进度条（列表即可，不需要图形化）
- 不修改 Build 阶段的 `TaskProgressPanel`

## Decisions

### D1: 双通道进度推送（emitProgress + plan_round_complete SSE 事件）

同时使用两个机制推送进度：

1. **`emitProgress()` 调用**：在 `runPlanStage()` 每个 round 结束后调用，更新 `AgentProgress` 对象。这让 `/api/agent/status` 端点能返回 plan 进度，支持页面刷新时从 API 恢复状态。
2. **`plan_round_complete` SSE 事件**：通过 EventBus emit，携带 `{ issueId, projectId, roundType, roundLabel, roundIndex, duration, verdict? }`。前端实时订阅，无需轮询。

**为什么两个都要：** 单靠 `emitProgress` 只能通过 API 轮询获取（5s 间隔），实时性差。单靠 SSE 事件在页面刷新后丢失状态。双通道互补：SSE 提供实时性，`AgentProgress` 提供恢复能力。

**Alternatives considered:**
- 纯前端从 `plan_round_start` 推断进度：脆弱，无法判断步骤是否完成、是否失败
- 只用 emitProgress + 轮询：5s 延迟不可接受，且无法携带 duration/verdict
- 只用 SSE 事件：页面刷新后状态丢失

### D2: EventMap 新增 plan_round_complete 事件类型

在 `event-bus.ts` 的 `EventMap` 中新增：

```typescript
plan_round_complete: {
  issueId: string;
  projectId: string;
  roundType: string;
  roundLabel: string;
  roundIndex: number;
  duration: number;      // seconds
  verdict?: 'PASS' | 'FAIL';
};
```

同步更新 4 个注册位置：`event-bus.ts` EventMap、`events.ts` `ALL_EVENT_TYPES`（如果存在）、`agent-events.ts` `AGENT_DETAIL_EVENTS`、`useSSE.tsx` `eventTypes`。

**Alternatives considered:**
- 复用 `plan_session_update` 加 `sessionUpdate: 'round_complete'`：语义不清晰，事件 payload 混杂，前端过滤复杂
- 使用 `plan_round_start` + 前端推断完成：无法可靠判断失败/超时

### D3: PlanProgressPanel 作为独立组件

新建 `PlanProgressPanel.tsx`，不修改 `TaskProgressPanel`。两者结构相似但数据源和步骤语义不同（Plan 步骤是固定序列，Build 任务是动态列表）。

组件接口：

```typescript
interface PlanStep {
  roundType: string;
  roundLabel: string;
  roundIndex: number;
  status: 'pending' | 'running' | 'completed' | 'failed';
  duration?: number;     // seconds
  verdict?: 'PASS' | 'FAIL';
}

interface PlanProgress {
  steps: PlanStep[];
  completedCount: number;
  totalSteps: number;    // base = 5
}
```

渲染位置：`SessionTimeline.tsx` 中，`currentStage === 'plan'` 且 `planProgress.steps.length > 0` 时，在 `TaskProgressPanel` 的同一位置渲染（PipelineStatusTimeline 下方、RoundSection 上方）。

**Alternatives considered:**
- 修改 `TaskProgressPanel` 适配 plan 步骤：两个场景差异大（固定步骤 vs 动态任务、verdict vs status），强行复用增加复杂度
- 在 `IssueDetailPage` 而非 `SessionTimeline` 中渲染：SessionTimeline 已有 SSE 订阅和 stage 感知，放这里更自然

### D4: useSessionTimeline 新增 planProgress state

在 `useSessionTimeline.ts` 中：

1. 新增 `planProgress` state（`PlanProgress | null`）
2. 监听 `plan_round_start` 事件：将对应步骤标记为 `running`
3. 监听 `plan_round_complete` 事件：更新步骤为 `completed`/`failed`，记录 duration 和 verdict
4. 从 `/api/agent/status` 的 `AgentProgress.taskProgress` 初始化：页面加载时根据 `completed` / `total` / `roundIndex` 构建初始步骤列表
5. 将 `planProgress` 加入 hook 返回值

**步骤列表初始化逻辑：**

```
BASE_STEPS = ['proposal', 'specs', 'design', 'tasks', 'self-review']
```

当收到 `plan_round_complete` 且 `verdict === 'FAIL'`（self-review 失败）时，向 `steps` 追加 `auto-fix` 和 `re-self-review` 两个步骤（status = pending）。

**Alternatives considered:**
- 纯从 SSE 事件重建状态（不存 API）：页面刷新丢失所有进度
- 将 planProgress 存入数据库：过度设计，plan 执行时间短（通常 <10 分钟），无需持久化

### D5: 后端 emitProgress 调用时机

在 `runPlanStage()` 中：

1. **Round 完成后**：每个 artifact round 成功后调用 `emitProgress({ stage: 'plan', roundType, roundIndex, taskProgress: { completed, total: 5 } })`
2. **Self-review 完成后**：根据 PASS/FAIL 分别处理
3. **Auto-fix 循环中**：auto-fix 和 re-self-review 完成后同样调用 `emitProgress`
4. **Checkpoint 恢复时**：`runPlanStage()` 开头加载 checkpoint 后，调用一次 `emitProgress` 反映已完成步骤

`emitProgress` 的调用点：

- `workflow-controller.ts` ~line 240（artifact round 成功后）
- `workflow-controller.ts` ~line 300（self-review PASS）
- `workflow-controller.ts` ~line 315（self-review FAIL）
- `workflow-controller.ts` ~line 370（auto-fix 完成）
- `workflow-controller.ts` ~line 410（re-self-review 完成）
- `workflow-controller.ts` ~line 125（checkpoint 恢复初始化）

### D6: Duration 计算方式

在后端 `runPlanStage()` 中，每个 round 开始时记录 `Date.now()`，round 完成时计算差值（秒），作为 `plan_round_complete` 事件的 `duration` 字段。不在前端计算，避免 SSE 延迟影响准确性。

## Risks / Trade-offs

**[plan_round_complete 事件丢失]** → 前端同时从 `/api/agent/status` 初始化进度（5s 轮询兜底），确保页面刷新后能恢复。SSE 断线重连时 useSSE 已有重连机制，重连后 agent/status 轮询会修正状态。

**[Auto-fix 循环步骤数量不确定]** → 基础步骤固定为 5 个，auto-fix/re-self-review 仅在 self-review FAIL 时追加。前端通过 `verdict === 'FAIL'` 触发追加，不预设固定数量。

**[Checkpoint 恢复时缺少 duration 数据]** → checkpoint 只记录已完成步骤列表，不保存各步骤耗时。恢复后已完成步骤不显示耗时，这是可接受的——用户更关心当前进度而非历史耗时。

**[4 个事件注册位置需同步]** → `plan_round_start` 已有这个模式且运行正常。新增 `plan_round_complete` 时在同样位置注册，并在代码注释中标注 4 处同步要求。

## Migration Plan

1. **后端先改**：先添加 `plan_round_complete` 到 EventMap + 4 个注册位置，再在 `runPlanStage()` 中添加 `emitProgress()` 调用和 `plan_round_complete` 事件发射
2. **前端后改**：添加 `planProgress` state 和 `PlanProgressPanel` 组件
3. **无需数据库迁移**：不新增表或字段
4. **无 breaking change**：`plan_round_complete` 是新事件，不影响现有 SSE 客户端；`emitProgress` 调用只填充已有的 `AgentProgress` 字段

## Open Questions

None.
