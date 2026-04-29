## Why

Plan 阶段运行时用户只能看到 SessionTimeline 的文本流，无法感知宏观进度——不知道总共几步、当前在第几步、还剩几步。后端已有 `emitProgress()` 方法但从未调用，前端缺少进度面板。Build 阶段有完善的 `TaskProgressPanel` + `ralph_task_update` 实时推送，Plan 阶段完全缺失对等的可观测性。

## What Changes

- 后端：`workflow-controller.ts` 每个 artifact round 完成后调用 `emitProgress()`，推送 `completedSteps / totalSteps`
- 后端：新增 `plan_round_complete` SSE 事件，步骤实际完成时 emit（取代纯前端推断）
- 前端：`useSessionTimeline.ts` 新增 `planProgress` state，监听 `plan_round_complete` 事件
- 前端：新增 `PlanProgressPanel` 组件，展示 plan 步骤列表（proposal → specs → design → tasks → self-review）及状态
- 前端：`IssueDetailPage` 在 `currentStage === 'plan'` 时渲染 `PlanProgressPanel`
- 步骤状态：pending / running / completed / failed，完成后显示耗时
- self-review FAIL 时展示 auto-fix → re-self-review 循环
- Checkpoint 恢复后已完成步骤直接标记为 completed

## Capabilities

### New Capabilities

- `plan-progress-tracking`: Plan 阶段步骤进度跟踪与实时推送（后端进度追踪 + SSE 事件 + 前端进度面板）

### Modified Capabilities

- `pipeline-session-events`: 新增 `plan_round_complete` SSE 事件类型
- `session-timeline-ui`: 集成 PlanProgressPanel，当 plan 阶段活跃时渲染进度面板
- `agent-session-ui`: SSE 订阅新增 `plan_round_complete` 事件类型

## Impact

- **后端**: `workflow-controller.ts`（调用 emitProgress、发射新事件）, `event-bus.ts`（新事件类型）, `agent-runner-service.ts`（AgentProgress 类型扩展）
- **前端**: `useSessionTimeline.ts`, 新增 `PlanProgressPanel.tsx`, `IssueDetailPage.tsx`, `SessionTimeline.tsx`
- **API**: `/api/agent/status` 响应新增 plan 阶段进度字段
- **SSE**: 新增 `plan_round_complete` 事件
- **无 breaking change**: 现有 `SessionTimeline`、`plan_session_update` 机制不变
