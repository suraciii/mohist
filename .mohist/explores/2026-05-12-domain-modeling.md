# Stage/Task/Check 领域建模与架构差距分析

## 背景

完成 #182（workflow_run 关系表落地）后，系统进入数据层关系化、业务层仍过程化的过渡态。本次探索的目标是统一 Stage/Task/Check 语义，识别当前设计与理想领域模型之间的差距，并产出可执行的重构路径。

## 统一语义（已确认）

| 概念 | 定义 | 判别标准 |
|------|------|---------|
| **Task** | 执行工作单元 | 可能对代码/文件产生变更；有"执行一次"语义 |
| **Check** | 验证条件 | 不修改代码；可重复运行；产出 pass/fail/pending |

> 执行 shell command（如 `npm run build`）不是区分标准。Health Gate 执行 build 是验证动作，不提交变更，因此是 Check。

## 统一后的四阶段结构

所有 Stage 遵循同一框架：`run() → executeTasks() → getChecks() → runChecksPhase()`

```yaml
Plan:
  tasks: [proposal, specs, design, tasks, self-review]
  checks: [health:plan, user-approval]
  fix-tasks: [repair-plan-artifacts, fix-plan-health]

Build:
  tasks: [task-1, task-2, ...]  # 动态 materialize 自 tasks.json
  checks: [all-tasks-complete, health:build]
  fix-tasks: [fix-build-health]

Check:
  tasks: [ai-review]
  checks: [review-passed, merge-ready, user-approval]
  fix-tasks: [fix-review-findings, repair-merge]

Integrate:
  tasks: [spec-sync, archive-change, merge]
  checks: [final-health]
  # 注意: Integrate 的 final-health 失败后没有 fix-task 路径
  # merge 后 worktree 冻结，只能失败或人工介入
```

## 当前架构异类：Integrate Stage

Integrate 是唯一不遵守 `BaseStageRunner` 框架的阶段：
- `getChecks()` 返回 `[]`
- `executeTasks()` 硬编码 4 个 step，不返回标准 `TaskResult[]`
- 各 step 自行 emit、自行 appendTaskResult、自行 `runFinalHealthGate()`
- 4 个 step 不写入 `workflow_tasks`，Web UI 看不到进度

这导致 Integrate 是"进度黑洞"。

## 数据层四套并行系统

| 系统 | 存储形式 | 状态 |
|------|---------|------|
| `tasks.json` + CheckSuites | 文件 + SQLite | Legacy 运行时 |
| `stage_executions` (v20) | JSON blob | 过渡中 |
| `stage_states` (v26) | 关系表 | 过渡中 |
| `workflow_runs` (v27) | 关系表 | **目标架构** |

一次 `ai-review` task 完成后，结果同时写入：review.md（文件）、stage_executions（JSON）、stage_states（投影）、workflow_tasks（mirror）。四个写入点无事务边界。

## 已创建的 Issue

- **#187** — Integrate Stage 标准化：将 step 迁移为标准 Task/Check 框架（p1）
- **#188** — WorkflowRun 聚合根重构：从事务脚本到领域模型（p0）

---

## 完整领域模型

### 核心聚合

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         WorkflowRun (Aggregate Root)                    │
│                         边界: 一次完整的 issue 处理                      │
├─────────────────────────────────────────────────────────────────────────┤
│  id: IssueNumber                                                        │
│  status: draft | running | paused | completed | failed | cancelled      │
│  stageOrder: StageName[]     # e.g. ['Plan', 'Build', 'Check', 'Integrate'] │
│  currentStage?: StageName                                               │
│                                                                         │
│  ┌─ StageRun[Plan] ─────────────────────────────────────────────┐      │
│  │  status: running                                             │      │
│  │  definition: StageDefinition (配置快照)                       │      │
│  │                                                              │      │
│  │  ┌─ Task[proposal] ──────────────────────────────────────┐   │      │
│  │  │  status: completed                                     │   │      │
│  │  │  attempts[1]: { output, metadata, duration }           │   │      │
│  │  └────────────────────────────────────────────────────────┘   │      │
│  │  ┌─ Task[specs] ─────────────────────────────────────────┐   │      │
│  │  │  status: running                                      │   │      │
│  │  │  attempts[1]: { startedAt, ... }                      │   │      │
│  │  └────────────────────────────────────────────────────────┘   │      │
│  │                                                              │      │
│  │  ┌─ Check[health:plan] ──────────────────────────────────┐   │      │
│  │  │  status: pending                                      │   │      │
│  │  │  runs: []                                             │   │      │
│  │  └────────────────────────────────────────────────────────┘   │      │
│  │                                                              │      │
│  │  policy: { "health:plan" → "fix-plan-health", maxAttempts:3}│      │
│  └──────────────────────────────────────────────────────────────┘      │
│                                                                         │
│  DomainEvent[] (持久化):                                                │
│  [WorkflowRunStarted, StageStarted(Plan), TaskStarted(proposal),       │
│   TaskCompleted(proposal), TaskStarted(specs), ...]                     │
└─────────────────────────────────────────────────────────────────────────┘
```

### 实体定义

#### WorkflowRun (聚合根)

**标识**: `IssueNumber`

**状态**: `draft` → `running` → `paused` / `completed` / `failed` / `cancelled`

**方法**:
- `start()` — 启动工作流，自动启动 `stageOrder[0]`
- `startStage(stage)` — 启动阶段（验证前置条件：前一个 stage 必须 completed）
- `completeTask(stage, taskId, result)` — 推进到下一个 task 或进入 checks
- `recordCheckResult(stage, checkName, result)` — 分类 check，决定继续/fix/fail
- `approve(stage)` / `reject(stage)` — 用户审批
- `pullEvents()` — 获取待持久化的事件

#### StageRun (Entity, 聚合内)

**标识**: `stage` (Plan/Build/Check/Integrate)

**状态机**:
```
PENDING → RUNNING → COMPLETED
            ↓
      AWAITING_APPROV → FAILED
            ↓ approve()
         RUNNING (恢复)
```

**方法**:
- `start()` — 启动第一个 pending task
- `advanceTask()` — 推进到下一个 task（内部）
- `enterChecks()` — 所有 task 完成，进入 checks（内部）
- `recordCheckResult(checkName, result)` — 分类结果，决定继续/fix/fail（内部）
- `triggerFixTask(checkName)` — 根据 policy 创建 fix task（内部）
- `approve()` / `reject()` — 审批响应

#### Task (Entity, 聚合内)

**标识**: `taskId`

**状态**: `pending` → `running` → `completed` / `failed`

**属性**:
- `taskId`, `title`, `status`, `order`
- `source`: 'definition' | 'dynamic' | 'fix'
- `causedBy?`: { checkName } — fix task 记录触发源
- `attempts`: TaskAttempt[]
- `currentAttempt?`: number

**方法**:
- `start()` — 创建新的 TaskAttempt
- `complete(output, metadata)` / `fail(error)`

#### TaskAttempt (Entity, Task 子实体)

**标识**: UUID

**属性**:
- `attemptNumber`
- `input`: { prompt?, command?, context? }
- `output?`, `error?`
- `metadata?`: Record<string, unknown> — Runner 可附加任意执行上下文
- `status`, `startedAt`, `completedAt`, `duration`

#### Check (Entity, 聚合内)

**标识**: `checkName`

**属性**:
- `checkName`, `title`, `status`, `order`
- `definition`: CheckDefinition
- `runs`: CheckRun[]
- `currentRun?`: number

**方法**:
- `run()` — 创建 CheckRun
- `recordResult(result, output?)`

#### CheckRun (Value Object)

每次 check 运行的独立记录。

- `runNumber`, `result`, `output?`, `startedAt`, `completedAt`

> `user-approval` 的 pending 不是 CheckRun 结果，而是 Check 状态。用户响应后创建新 CheckRun（passed/failed）。

### 值对象

| 值对象 | 内容 |
|--------|------|
| `StageDefinition` | `stage`, `tasks: TaskDefinition[]`, `checks: CheckDefinition[]`, `policy: CheckFailurePolicy` |
| `TaskDefinition` | `taskId`, `title`, `description?`, `executor: 'ai-agent' \| 'shell' \| 'builtin'` |
| `CheckDefinition` | `checkName`, `title`, `evaluator: 'static-analysis' \| 'health-gate' \| 'user-approval' \| 'builtin'` |
| `CheckFailurePolicy` | `fixes: Record<string, string>`, `maxAttempts`, `onExhausted` |
| `TaskResult` | `status`, `output?`, `error?`, `metadata?` |
| `CheckResult` | `status: 'passed' \| 'failed' \| 'pending'`, `output?` |

### 领域不变式

| # | 不变式 | 说明 |
|---|--------|------|
| I1 | **顺序性** | tasks 必须按 order 顺序执行，前一个 completed 前后一个不能 start |
| I2 | **Task-Check 边界** | 所有 tasks completed 前 checks 不能 start；所有 checks passed 前 StageRun 不能 completed |
| I3 | **单 Stage 运行** | WorkflowRun 同时只能有一个 running 的 StageRun。awaiting_approval 时 WorkflowRun 整体 paused |
| I4 | **审批一致性** | 任一 Check status === 'pending' 时，所属 StageRun 必须是 awaiting_approval |
| I5 | **Fix 溯源** | fix-task 的 source 必须是 'fix'，且 causedBy 指向触发它的 check |
| I6 | **尝试上限** | Task 的 attempts.length 不能超过 policy.maxAttempts，超过时 StageRun 进入 failed |
| I7 | **Stage 准入** | `startStage(stage)` 要求 `stageOrder` 中该 stage 的前置 stage 必须 completed |
| I8 | **Check 幂等** | Check 不修改代码/文件 |
| I9 | **Integrate 冻结** | Integrate 的 `merge` task 完成后，worktree 冻结，不允许再修改代码。`final-health` Check 失败 → StageRun 直接 failed，无 fix-task 路径 |
| I10 | **无回滚** | Mohist 不回滚任何副作用，所有改动都是追加的、被 git 版本管理的。rerun/rewind 是状态调整，不是副作用撤销 |

### 领域事件

```
Workflow 级别:
  WorkflowRunStarted, WorkflowRunCompleted, WorkflowRunFailed, WorkflowRunCancelled

Stage 级别:
  StageStarted, StageCompleted, StageFailed(reason, causedBy),
  StageAwaitingApproval, StageApproved, StageRejected

Task 级别:
  TaskStarted, TaskCompleted, TaskFailed, FixTaskTriggered

Check 级别:
  CheckStarted, CheckPassed, CheckFailed, CheckPendingApproval
```

### 数据库映射

| 领域对象 | 数据库表 |
|---------|---------|
| WorkflowRun | `workflow_runs` |
| StageRun | `workflow_stage_runs` |
| Task | `workflow_tasks` |
| TaskAttempt | `workflow_task_attempts` |
| Check | `workflow_checks` |
| CheckRun | `workflow_check_runs` |
| DomainEvent | `workflow_events` |

### 当前代码映射

| 当前代码 | 领域模型位置 | 状态 |
|---------|-------------|------|
| `workflow-run-service.ts` | `WorkflowRunRepository` + `WorkflowRun` | 散装 CRUD → 聚合根方法 |
| `base-stage-runner.ts` | `StageRunner` 接口 + `StageRun` 实体 | Runner 既管执行又管状态 → 拆分 |
| `plan-stage-runner.ts` | `PlanRunner implements StageRunner` | 只需保留 executeTask / evaluateCheck |
| `workflow_tasks` 表 | `Task` + `TaskAttempt` | 缺 `task_attempts` 表 |
| `workflow_checks` 表 | `Check` + `CheckRun` | 缺 `check_runs` 表 |
| `stage_executions` | 废弃 | 迁移后删除 |
| `check_suites` | 废弃 | CheckRun 承载 |
| `tasks.json` | 废弃 | Build task definitions 写入 workflow_tasks |
| `review.md` | 无副作用 | task output 的表现形式 |

---

## 重构路线图

```
Layer 1: 聚合根封装（影响所有后续）
    └── WorkflowRun 成为真正聚合根
    └── Runner 只发命令，不直接操作 DB/FS

Layer 2: Stage 定义配置化（解锁可扩展性）
    └── 提取 StageDefinition，与 Runner 分离
    └── #187 的 task/check 从配置生成

Layer 3: 历史与追踪（解锁可观测性）
    └── TaskAttempt / CheckRun 表

Layer 4: 领域事件（解锁审计与恢复）
    └── 持久化领域事件日志
```

---

## 事件风暴

按时间线排列命令、聚合、事件，识别策略、外部系统和读模型。

### Phase 1: 启动工作流

```
[Command]               [Aggregate]         [Event]
StartWorkflowRun    →   WorkflowRun     →   WorkflowRunStarted
                                                │
                                                ▼
[Policy] "自动启动 stageOrder[0]"
                                                │
                                                ▼
[Command]               [Aggregate]         [Event]
StartStage          →   StageRun        →   StageStarted(stageOrder[0])
                                                │
                                                ▼
[Policy] "自动启动第一个 task"
                                                │
                                                ▼
[Command]               [Aggregate]         [Event]
StartTask           →   Task            →   TaskStarted
                                                │
                                                ▼
[Read Model]  Web UI 显示 "Stage: task 运行中"
```

> **关键**: WorkflowRun 不硬编码 Plan，启动的是 `stageOrder[0]`。

### Phase 2: Task 执行与完成

```
[External System]       AI Agent / Shell / RalphExecutor
                        │
                        └── 执行 task
                        └── 修改代码 / 生成文件
                        │
                        ▼
[Command]               [Aggregate]         [Event]
CompleteTask        →   Task            →   TaskCompleted
                        (output, metadata)
                                                │
                                                ▼
[Policy] "Task 完成 → 推进 Stage"
                                                │
                                                ▼
[Command]               [Aggregate]         [Event]
AdvanceStage        →   StageRun        →   TaskStarted(next)
```

> **发现**: Task 完成后的推进是**策略**，不是 Runner 的逻辑。

### Phase 3: 所有 Task 完成 → 进入 Checks

```
[Event] TaskCompleted(lastTask)
            │
            ▼
[Policy] "所有 tasks completed → 进入 checks"
            │
            ▼
[Command]               [Aggregate]         [Event]
EnterChecks         →   StageRun        →   CheckStarted
                                                │
                                                ▼
[External System]       Health Gate / Static Analysis / User
                        │
                        └── 执行验证
                        │
                        ▼
[Command]               [Aggregate]         [Event]
RecordCheckResult   →   Check           →   CheckPassed / CheckFailed
```

### Phase 4: Build 动态 Tasks

```
[Event] StageStarted(Build)
            │
            ▼
[Policy] "Build Stage 从 tasks.json materialize tasks"
            │
            ▼
[Command]               [Aggregate]         [Event]
MaterializeTasks    →   StageRun        →   TasksMaterialized
            │
            ▼
[Command]               [Aggregate]         [Event]
StartTask           →   Task            →   TaskStarted
```

> **发现**: `MaterializeTasks` 是当前 `workflowRunService.materializeBuildTasks()` 的散装操作，应封装为 `StageRun` 的领域命令。

### Phase 5: Check 失败 → Fix Task 触发

```
[Event] CheckFailed(review-passed)
            │
            ▼
[Policy] "CheckFailed + policy 有 fix → 触发 FixTask"
            │
            ▼
[Command]               [Aggregate]         [Event]
TriggerFixTask      →   StageRun        →   FixTaskTriggered
            │           (checkName → fixTaskId)
            ▼
[Policy] "FixTask 触发 → 插入 task 队列，重新进入 task 阶段"
            │
            ▼
[Command]               [Aggregate]         [Event]
StartTask           →   Task            →   TaskStarted(fix-task)
```

> **关键发现**: `CheckFailed` → `FixTaskTriggered` 是显式**策略**，由 `CheckFailurePolicy` 决定。当前硬编码在 `BaseStageRunner.handleCheckFailure()` 中，应提取为策略对象。

### Phase 6: Stage 完成 → 推进 Workflow

```
[Event] StageCompleted(current)
            │
            ▼
[Policy] "Stage completed → 启动 stageOrder 中的下一个 stage"
            │
            ▼
[Command]               [Aggregate]         [Event]
StartStage(next)    →   StageRun        →   StageStarted(next)
            │
            ▼
[Policy] "如果 stageOrder 没有下一个 stage → Workflow 完成"
            │
            ▼
[Command]               [Aggregate]         [Event]
CompleteWorkflow    →   WorkflowRun     →   WorkflowRunCompleted
```

> **关键**: 不硬编码 "Plan → Build → Check → Integrate"，全部从 `stageOrder` 顺序推导。

### Phase 7: 审批流程

```
[Event] CheckPendingApproval
            │
            ▼
[Command]               [Aggregate]         [Event]
AwaitApproval       →   StageRun        →   StageAwaitingApproval
            │           status → awaiting_approval
            ▼
[External System]       Human (User)
            │
            └── 用户在 Web UI 点击 Approve / Reject
            │
            ▼
[Command]               [Aggregate]         [Event]
Approve             →   StageRun        →   StageApproved
            │
            ▼
[Policy] "审批通过 → 重新运行 checks"
            │
            ▼
[Command]               [Aggregate]         [Event]
EnterChecks         →   StageRun        →   CheckStarted
```

### Phase 8: Integrate 特殊流程

```
[Event] StageStarted(Integrate)
            │
            ▼
[Command]               [Aggregate]         [Event]
StartTask(spec-sync) →  Task[spec-sync] →   TaskStarted(spec-sync)
            │
            ▼
[External System]       File System
            │
            └── 写 specs/ 目录
            │
            ▼
[Command]               [Aggregate]         [Event]
CompleteTask        →   Task[spec-sync] →   TaskCompleted(spec-sync)
            │
            ▼
[Command]               [Aggregate]         [Event]
StartTask(archive)  →   Task[archive-change] → TaskStarted(archive-change)
            │
            ▼
[External System]       File System
            │
            └── mv .mohist/issues/N .mohist/archive/N
            │
            ▼
[Command]               [Aggregate]         [Event]
CompleteTask        →   Task[archive-change] → TaskCompleted(archive-change)
            │
            ▼
[Command]               [Aggregate]         [Event]
StartTask(merge)    →   Task[merge]     →   TaskStarted(merge)
            │
            ▼
[External System]       Git
            │
            └── git merge
            │
            ▼
[Command]               [Aggregate]         [Event]
CompleteTask        →   Task[merge]     →   TaskCompleted(merge)
            │
            ▼
[Policy] "merge 完成 → worktree 冻结"
            │
            ▼
[Command]               [Aggregate]         [Event]
EnterChecks         →   StageRun        →   CheckStarted(final-health)
            │
            ▼
[External System]       Health Gate
            │
            └── npm run build
            │
            ▼
[Command]               [Aggregate]         [Event]
RecordCheckResult   →   Check           →   CheckFailed(final-health)
            │
            ▼
[Policy] "Integrate Check 失败 → 直接 StageFailed，无 fix 路径"
            │
            ▼
[Command]               [Aggregate]         [Event]
FailStage           →   StageRun        →   StageFailed(final-health)
            │           (reason: 'check-unrepaired')
            ▼
[Policy] "Integrate 失败 → WorkflowFailed，无法恢复"
            │
            ▼
[Command]               [Aggregate]         [Event]
FailWorkflow        →   WorkflowRun     →   WorkflowRunFailed
```

> **关键**: Integrate 的 `merge` task 完成后 worktree 冻结。`final-health` 失败意味着代码已合并到 main 但存在问题，只能人工介入。没有 fix-task、没有 recheck。

---

### 识别出的策略（Policies）

| 策略 | 触发事件 | 动作 | 当前代码位置 |
|------|---------|------|-------------|
| P1: 自动启动首 Stage | `WorkflowRunStarted` | `startStage(stageOrder[0])` | `WorkflowRun.start()` |
| P2: Task 完成推进 | `TaskCompleted` | `advanceTask()` 或 `enterChecks()` | `BaseStageRunner` 内部 |
| P3: 进入 Checks | 所有 Task `completed` | `enterChecks()` | `BaseStageRunner` 内部 |
| P4: Check 失败修复 | `CheckFailed` + policy 有 fix | `triggerFixTask()` | `BaseStageRunner.handleCheckFailure()` |
| P5: Stage 完成推进 | `StageCompleted` | `startStage(next)` | 各 Runner `run()` 方法 |
| P6: 审批后继续 | `StageApproved` | 重新 `enterChecks()` | `BaseStageRunner.handleApprovalCheck()` |
| P7: Integrate 冻结 | `TaskCompleted(merge)` | worktree 冻结，禁止后续修改 | `IntegrateStageRunner` 内部 |
| P8: Integrate 失败终结 | `CheckFailed(final-health)` | `StageFailed` → `WorkflowRunFailed` | `BaseStageRunner` 内部 |

### 识别出的外部系统

| 外部系统 | 用途 |
|---------|------|
| **AI Agent** | 执行 Plan/Build/Check 的 AI task |
| **RalphExecutor** | 执行 Build 编码任务 |
| **Shell / Health Gate** | 运行 `npm run build` 等 |
| **File System** | Integrate 的 spec-sync、archive |
| **Git** | Integrate 的 merge |
| **Human (User)** | user-approval Check |

### 识别出的读模型（Read Models）

| 读模型 | 数据来源 | 用途 |
|--------|---------|------|
| **Issue Pipeline 状态** | `WorkflowRun.status` + `StageRun.status` | Web UI 看板 |
| **Stage 进度详情** | `Task[]` + `Check[]` | Web UI Stage 详情页 |
| **Task 执行历史** | `TaskAttempt[]` | 调试、审计 |
| **Check 运行历史** | `CheckRun[]` | 质量分析 |

---

### 事件风暴发现的新问题

1. **`all-tasks-complete` 的归属**
   Build 的 `all-tasks-complete` Check 验证的是"其他 task 是否都完成了"。这不是一个 Check，而是 **P3 策略** 的一部分。所有 Stage 都遵循"tasks 全部完成后再运行 checks"，这是 `StageRun` 的状态机规则，不是可选 Check。应从 Build 的 Check 列表中移除。

2. **`MaterializeTasks` 是领域命令**
   Build Stage 启动时从 `tasks.json` 读取并创建 Task 实体。`MaterializeTasks` 是显式领域命令，应由 `StageRun` 在 `start()` 时根据 `StageDefinition` 和外部输入执行。当前 `workflowRunService.materializeBuildTasks()` 是散装 CRUD。

3. **`StageDefinition` 与运行时分离**
   Plan 的 task 定义时已知，Build 的 task 运行时 materialize，Check 的 task 只有一个，Integrate 的 task 定义时已知。`StageDefinition` 是"配置蓝图"，`StageRun` 是"运行时实例"。Build 的 `StageDefinition` 包含 task 模板（如 `executor: 'ralph'`），`MaterializeTasks` 根据外部输入实例化具体 Task。这样 Build 和 Plan 的区别只是"Task 来源不同"，不是架构差异。

4. **Integrate 的 Check 失败不可修复**
   `merge` task 完成后代码已进入 main，无法 fix-task + recheck。`final-health` Check 失败 = 整个 Workflow 失败。这与 Plan/Build/Check 的"check 失败 → fix → recheck"循环完全不同。`CheckFailurePolicy` 对 Integrate 不生效（或 `onExhausted: 'fail-stage'` 是唯一选项）。
