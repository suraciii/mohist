# WorkflowRun 聚合根领域建模

## 结论

#182 已经落地 `workflow_runs` / `workflow_stage_runs` / `workflow_tasks` / `workflow_checks` 关系表，但当前 Workflow 仍然缺少业务层一致性边界。真正要解决的问题不是“有没有表”，而是：

**谁有权改变 workflow 状态？**

最终模型应遵循：

```
Task executes.
Check verifies.
Runner reports.
WorkflowRun decides.
Repository persists.
Projection displays.
```

中文表达：

- Task 负责执行，允许产生副作用
- Check 负责验证，不修改代码/文件
- Runner 负责调用外部系统，并汇报结果
- WorkflowRun / StageRun 负责决定状态推进
- Repository 负责事务化保存
- Projection / API / UI 负责展示，不反推业务真相

P0 范围应先解决 **状态转换不能绕过聚合根**，不要把事件溯源、完整 attempt 历史、配置化 DSL 一次性做完。

## 当前与目标

```
Current:
  WorkflowEngine / BaseStageRunner / StageRunner
      ├── direct issue stage update
      ├── stage_executions JSON
      ├── stage_states projection
      └── WorkflowRunService CRUD

Target:
  Runner executes side effects
      ↓ reports result
  WorkflowRun aggregate decides state transition
      ↓ repository transaction
  workflow_runs / stage_runs / tasks / checks
      ↓ projections/API/UI
  issue stage + stage-state read models
```

当前实现已经 seed 了 Integrate tasks/check：

- `integrate:spec-sync`
- `integrate:archive-change`
- `integrate:merge`
- `health:integrate`

因此 Integrate 的问题不是“完全看不到进度”，而是 step 顺序、失败处理、merge 后冻结规则仍硬编码在 Runner 中，没有交给 `StageRun` 统一推进。

## 领域边界

### Task

Task 是执行工作单元，有“执行一次”的语义，可能产生代码、文件、git、agent session 等副作用。

例子：

- Plan: `proposal`, `specs`, `design`, `tasks`, `self-review`
- Build: 从 `tasks.json` materialize 出来的具体实现任务
- Check: `ai-review`, `repair-review-findings`, `repair-merge`
- Integrate: `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`

### Check

Check 是验证条件，应可重复运行，不应修改代码或文件。运行 shell command 不影响它是不是 Check；例如 health gate 虽然执行 `npm run build`，但它是验证动作，不提交变更。

例子：

- `health:plan`
- `health:build`
- `review-passed`
- `merge-ready`
- `user-approval`
- `health:integrate`

### Runner

Runner 仍然负责执行外部副作用：写 artifact、调用 agent、运行 shell、调用 git、archive change。

Runner 不再负责：

- 决定 stage 是否 passed/failed
- 决定下一个 stage
- 直接 UPDATE workflow 状态表
- 管理 task/check 顺序推进

Runner 的职责应变成：执行 task/check 定义对应的外部动作，然后把结果汇报给 `WorkflowRun.completeTask(...)` 或 `WorkflowRun.recordCheckResult(...)`。

### Projection

Issue stage、stage-state、Web UI、API response 都是 projection/read model。

Projection 可以落后、可以重建、可以兼容旧数据；但它不能成为 workflow 的业务真相来源。

## 领域模型

```
WorkflowRun
  id
  issueId
  stageOrder
  status
  currentStage
  stageRuns[]

StageRun
  stage
  status
  tasks[]
  checks[]
  policies
  freezePoint?

TaskRun
  taskId
  title
  order
  source: definition | dynamic | fix
  status
  causedBy?
  output
  metadata

CheckState
  checkName
  title
  order
  status
  message
  output
  runCount

StageDefinition
  stage
  taskDefinitions
  checkDefinitions
  checkFailurePolicies
```

## 聚合根职责

`WorkflowRun` 是一次 issue pipeline 执行的唯一一致性边界。

它负责：

- 从 `stageOrder` 推导 stage 准入和下一阶段
- 保证同时只有一个 active/running stage
- 处理 workflow completed/failed/cancelled
- 将 stage 完成推进到下一 stage
- 对外产出可投影的 decision/events

`StageRun` 负责：

- task 顺序推进
- checks 阶段进入条件
- check failure 分类
- fix-task 插入与溯源
- Build 阶段动态 materialize tasks
- Integrate merge 后 freeze 规则
- approval pending / approved / rejected 状态转换

## 事件风暴

### 1. StartWorkflow

```
[Command] StartWorkflow
      │
      ▼
[Aggregate] WorkflowRun
      │
      ├─ validate no active run
      ├─ create StageRuns from stageOrder
      └─ start stageOrder[0]
      │
      ▼
[Events] WorkflowStarted, StageStarted(plan), TaskReady(proposal)
```

策略：

- 第一个 stage 来自 `stageOrder[0]`
- 不由 `WorkflowEngine` hardcode `Backlog -> Plan`
- `Issue.stage` 只是投影，不是状态机决策入口

### 2. CompleteTask

```
[External] Runner executes task side effect
      │
      ▼
[Command] CompleteTask(stage, taskId, result)
      │
      ▼
[Aggregate] StageRun
      │
      ├─ ensure task is current runnable task
      ├─ record result
      ├─ if failed: fail stage
      ├─ if next task exists: start next task
      └─ if all tasks completed: enter checks
      │
      ▼
[Events] TaskCompleted | TaskFailed | TaskReady(next) | ChecksReady
```

策略：

- task 顺序是领域规则，不是 Runner 循环的私有规则
- Runner 可以顺序执行外部动作，但每一步完成都必须汇报给聚合根
- skipped 也是 task result，需要由聚合根判定是否允许

### 3. RecordCheckResult

```
[External] Check evaluator reads facts
      │
      ▼
[Command] RecordCheckResult(stage, checkName, result)
      │
      ▼
[Aggregate] StageRun
      │
      ├─ pass: mark check passed
      ├─ pending approval: await approval
      ├─ repairable fail: schedule fix task
      └─ unrepaired fail: fail stage
      │
      ▼
[Events] CheckPassed | ApprovalRequested | FixTaskScheduled | StageFailed
```

策略：

- Check 不修改代码/文件
- 修复动作必须转成 Task
- `review.md` 生成、删除、重新生成都不是 Check 行为

### 4. ApproveStage

```
[Command] ApproveStage(stage, output)
      │
      ▼
[Aggregate] StageRun
      │
      ├─ ensure stage is awaiting approval
      ├─ record approval
      └─ continue stage completion
      │
      ▼
[Events] StageApproved, StageCompleted | ChecksReady
```

策略：

- approval 是用户输入，不是一个会执行副作用的 Check
- 审批通过后是否需要重新检查，由 StageRun 策略决定
- Check 阶段审批前如果需要创建 convergence commit，应建模为 Task 或 approval command side effect，而不是藏在 Check handling 里

### 5. AdvanceStage

```
[Event] StageCompleted(current)
      │
      ▼
[Policy] derive next stage from stageOrder
      │
      ▼
[Aggregate] WorkflowRun
      │
      ├─ if next exists: start next stage
      └─ else: complete workflow
      │
      ▼
[Events] StageStarted(next) | WorkflowCompleted
```

策略：

- `StageRunResult.nextStage` 应消失
- `WorkflowEngine` 不再决定下一阶段
- issue status/stage 更新是 projection

### 6. MaterializeBuildTasks

```
[Event] StageStarted(build)
      │
      ▼
[External Input] tasks.json
      │
      ▼
[Command] MaterializeTasks(build, tasks)
      │
      ▼
[Aggregate] StageRun(build)
      │
      ├─ create dynamic task list
      └─ start first runnable task
      │
      ▼
[Events] TasksMaterialized, TaskReady(T-001)
```

策略：

- `tasks.json` 是 Plan artifact 和 Build 输入
- `workflow_tasks` 是运行时 truth
- 不再依赖回写 `tasks.json.passes/error` 判断运行进度

### 7. IntegrateMergeCompleted

```
[Command] CompleteTask(integrate, integrate:merge, result)
      │
      ▼
[Aggregate] StageRun(integrate)
      │
      ├─ record landedSha / targetBranch / candidateHeadSha
      ├─ mark merge task completed
      └─ freeze worktree
      │
      ▼
[Events] TaskCompleted(integrate:merge), DeliveryEffectRecorded, WorktreeFrozen
```

策略：

- merge 是交付副作用，不能被隐藏
- landed sha 是用户/API/UI 必须能感知的事实
- freeze 是领域状态，不是 Runner 注释

### 8. PostMergeHealthFailed

```
[Command] RecordCheckResult(integrate, health:integrate, failed)
      │
      ▼
[Aggregate] StageRun(integrate)
      │
      ├─ detect freeze point already crossed
      ├─ forbid fix task
      └─ fail stage/workflow
      │
      ▼
[Events] CheckFailed(health:integrate), StageFailed(post-merge-health-failed), WorkflowFailed
```

策略：

- `health:integrate` 无论配置如何都不能触发 `fix-integrate-health`
- 失败不是普通 check-unrepaired，而是 post-merge delivery failure
- 用户需要看到：merge 已发生，失败在 post-merge health，需要人工介入

## SideEffect 建模决策

不新增独立 `SideEffect` 实体。Mohist 不做副作用回滚，副作用应作为 task result 的事实记录下来：

```typescript
TaskRun {
  taskId: 'integrate:merge',
  status: 'completed',
  metadata: {
    targetBranch,
    baseSha,
    candidateHeadSha,
    landedSha,
    rebased,
  },
}
```

这样领域模型能表达：

- 副作用已经发生
- 后续状态推进受这个事实约束
- UI/API 可以展示这个事实
- rerun/rewind 不暗示撤销副作用

## P0 实施切片

P0 不做完整 event store。先做：

```
WorkflowApplicationService
  load WorkflowRun
  ask Runner to execute current task/check
  call aggregate method
  save aggregate transactionally
  update projections
```

示意：

```typescript
const workflowRun = repo.load(issue.id)
const execution = await runner.execute(commandFrom(workflowRun))
const decision = workflowRun.completeTask(stage, taskId, execution.result)
repo.save(workflowRun)
projection.apply(decision.events)
```

P0 成功的标志：

- 不再有外部代码直接调用 `setStagePassed` / `setStageFailed` 决定业务状态
- 不再由 `StageRunResult.nextStage` 推进 issue stage
- BaseStageRunner 不再拥有 check failure/fix/recheck/stage completion 的最终决策权
- projections 可以落后或重建，但业务 truth 来自 WorkflowRun

## 必须保持的不变式

```
I1: Stage 准入 — startStage(stage) 必须要求前置 stage completed
I2: 单 Stage 运行 — 一个 WorkflowRun 同时只能有一个 running/awaiting_approval 的 StageRun
I3: Task 顺序 — task 必须按 order 推进，前一个未完成时后一个不能 start
I4: Task-Check 边界 — 所有 tasks completed 前 checks 不能 start
I5: Check 幂等 — Check 不修改代码/文件；会修改文件的动作必须建模为 Task
I6: Fix 溯源 — fix-task 必须记录 causedBy check/task
I7: 审批一致性 — user-approval pending 时 StageRun 必须 awaiting_approval
I8: Stage 完成 — 所有 required checks passed 后 StageRun 才能 completed
I9: Integrate 冻结 — merge 完成后不得再自动修改代码
I10: 无回滚 — Mohist 不回滚副作用；rerun/rewind 只是状态调整，不暗示撤销副作用
```

## 后续扩展

这些能力有价值，但不属于 #188 的 P0：

- TaskAttempt 明细
- CheckRun 明细
- DomainEvent 持久化
- 事件投影/审计/恢复
- YAML/DSL workflow 定义

## 对 #188 的实施要求

- `WorkflowRunService.setStagePassed/setStageFailed/setStageAwaitingApproval` 这类绕过聚合根的写入口应删除或降级为 repository 内部实现
- `WorkflowEngine` 不再根据 `StageRunResult.nextStage` 自行推进 stage
- `BaseStageRunner` 不再直接决定 stage pass/fail/awaiting-approval
- Build tasks 从 `tasks.json` materialize 到 `workflow_tasks` 后，运行时进度以 `workflow_tasks` 为准
- `all-tasks-complete` 不再作为 Build 业务 Check 执行，而是 `StageRun.enterChecks()` 的前置条件
- Check 不修改文件系统或 git；有副作用的动作被建模为 Task
- Integrate merge 完成后 `health:integrate` 失败不会触发任何 fix-task，WorkflowRun 直接进入 failed/manual-intervention
- UI/API 可以清楚展示 Integrate 已完成的 delivery side effects，包括 archive/spec-sync/merge 结果和 landed sha
