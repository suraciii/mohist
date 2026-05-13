# Stage Runner 统一化 — 改动依赖深度分析

**日期**: 2026-05-13
**分析目标**: 确定 9 个 issue 之间的真实依赖关系，排出可执行的实施顺序

---

## 一、每个 Issue 的前置条件和后置影响

### Issue #1: Fix Task 统一

**前置条件**: 无（可独立开始）

**涉及代码**:
- `workflow/health-fix-task.ts` (195行) — 独立函数
- `workflow/review-fix-task.ts` (182行) — 独立函数  
- `workflow/plan-repair-task.ts` (271行) — 独立函数
- 各 runner 的 `executeReportedTask` 方法中的 if/else 分支

**调用关系**:
```
BaseStageRunner.tryLegacyRepair()
  └── runFixTask(ctx, policy.fixTaskId, failedCheck, attempt)
       └── [默认] executeReportedTask() → null
       └── [BuildStageRunner 覆盖] if taskId === 'fix-build-health' → runHealthFixTask()
       └── [CheckStageRunner 覆盖] if taskId === 'fix-review-findings' → runReviewFixTask()
       └── [PlanStageRunner 覆盖] if taskId === 'repair-plan-artifacts' → runPlanRepairTask()
       └── [IntegrateStageRunner 覆盖] if taskId === 'fix-integrate-health' → runHealthFixTask()
```

**关键发现**:
- Fix Task 当前是"函数级别"的复用（health-fix-task 被 Plan/Build/Integrate 共享）
- 但调用点分散在 4 个 runner 的 `executeReportedTask` 中
- 统一后变为"Handler 级别"复用：FixTaskHandler 注册到 runner，runner 通过配置调度

**隐藏依赖**:
- Fix Task 的 `buildXxxPrompt` 函数依赖 `StageContext`（issue, worktreePath 等）
- 统一后 Handler 仍然需要 `StageContext`，接口不变
- **测试影响**: 现有测试通过 mock `executeReportedTask` 来测试 fix task 逻辑，统一后需要改为 mock `FixTaskHandler`

**结论**: 
- **可以 Phase 1 做**，但有两种路径：
  - **路径 A（渐进）**: 先提取 `FixTaskHandler`，当前 runner 的 `executeReportedTask` 改为调用 FixTaskHandler（适配器模式）
  - **路径 B（等待）**: 等 runner 配置化后，Fix Task 从配置自动调度，删除 `executeReportedTask`
- **推荐路径 A**：先提取，减少 runner 里的硬编码

---

### Issue #2: emitSafe / writeLog 上移

**前置条件**: 无（完全独立）

**涉及代码**:
- `workflow/build-stage-runner.ts` 第370-395行（私有方法）
- `workflow/plan-stage-runner.ts`（类似私有方法）
- 其他 runner 中的重复实现

**调用关系**:
```
各 StageRunner.executeTasks()
  ├── this.emitSafe(eventBus, 'xxx_stage_started', {...})
  ├── this.writeLog(workflowLogRepo, issueId, 'xxx_started', {...})
  ├── ...（执行中多次调用）
  ├── this.emitSafe(eventBus, 'xxx_stage_completed', {...})
  └── this.writeLog(workflowLogRepo, issueId, 'xxx_completed', {...})
```

**关键发现**:
- 每个 runner 复制了完全一样的 `emitSafe` 和 `writeLog` 逻辑
- 只是调用点不同（stage 名称不同）
- 上移后 `StageContext` 变成：
  ```typescript
  ctx.emit('stage_started', { stage: 'build', ... });
  ctx.log('stage_started', { stage: 'build', ... });
  ```

**隐藏依赖**:
- **零依赖**：这是纯重构，行为完全不变
- **测试影响**: 测试中使用 `vi.spyOn(eventBus, 'emit')` 的仍然工作，只是 spy 对象从 `eventBus` 变成 `ctx.emit`
- 但 `base-stage-runner.test.ts` 使用 `TestStageRunner`，如果 `emitSafe` 上移到 `StageContext`，测试需要注入 mock `StageContext`

**结论**: **最先做**，零风险，为后续 runner 改造减少重复代码

---

### Issue #3: Task Handler 提取

**前置条件**: 
- #2（推荐，但不是必须）：emitSafe 上移后 Handler 代码更简洁
- **Handler 接口定义需要先确定**（这是真正的前置条件）

**涉及代码**:
- `openspec/ralph-executor.ts` (1086行) → 拆分为 RalphTaskLoader + RalphTaskHandler
- `agent-runtime/agent-session.ts` → 封装为 AgentSessionTaskHandler
- 各 runner 的 `executeTasks()` → 改为调用 Handler

**调用关系（当前）**:
```
PlanStageRunner.executeTasks()
  ├── AgentSession.create() → session.execute(proposalPrompt)
  ├── session.execute(specsPrompt)
  ├── session.execute(designPrompt)
  ├── session.execute(tasksPrompt)
  └── session.execute(selfReviewPrompt)

BuildStageRunner.executeTasks()
  └── RalphExecutor.execute(change)
      └── runRalphLoop()
          ├── readTasks() → findNextPendingTask() → buildTaskContext()
          ├── withSession({ task: prompt })  ← 每个 task 独立 session
          ├── categorizeFailure() → retry
          └── update tasks.json progress

CheckStageRunner.executeTasks()
  └── AgentSession.create() → session.execute(reviewPrompt)

IntegrateStageRunner.executeTasks()
  ├── OpenSpecIntegrator.apply()
  ├── artifactManager.archiveChange()
  └── worktreeManager.mergeApprovedCandidate()
```

**关键发现**:
- RalphExecutor 的 `runRalphLoop` 被 **8 个测试文件** 直接导入：
  - `tests/ralph-executor.test.ts` (1456行)
  - `tests/ralph-executor-logging.test.ts`
  - `tests/ralph-executor-auto-skip.test.ts`
  - `tests/build-workflowrun-tasks.test.ts`
  - `tests/workflow/workflow-session-handling.test.ts`
  - `tests/wip-commit.test.ts`
  - `tests/pipeline-checkpoint.test.ts` (mock)
  - `tests/build-pipeline-observability.test.ts` (mock)
- 这些测试直接调用 `runRalphLoop`, `setAcpSessionRunner`, `RalphExecutor.execute()` 等
- **拆分 RalphExecutor 后，这些测试需要更新**

**隐藏依赖**:
- `RalphTaskHandler` 需要 `categorizeFailure`, `FAILURE_CATEGORY_CONFIGS`, `storeFailureLearning`, `buildTaskContext` 等函数
- 这些函数目前在 `ralph-executor.ts` 中，拆分后需要决定放在哪里
- **建议**: 创建一个 `ralph-task-utils.ts` 模块，存放这些共享函数

**测试迁移策略**:
```
当前测试:
  import { runRalphLoop, setAcpSessionRunner } from 'ralph-executor'
  const result = await runRalphLoop(change, context, options)

改造后测试:
  import { RalphTaskHandler } from 'ralph-task-handler'
  import { RalphTaskLoader } from 'ralph-task-loader'
  const tasks = await RalphTaskLoader.load(ctx, [])
  const handler = new RalphTaskHandler()
  const result = await handler.execute(tasks[0], ctx)
```

**结论**: 
- **可以和 #1 并行**
- Handler 接口定义好后，#1 的 FixTaskHandler 也可以复用这个接口
- 但 RalphExecutor 拆分测试影响大，需要单独一个 PR

---

### Issue #4: Task Loader

**前置条件**: 
- **#3 必须完成**（需要知道 Handler 接口才能定义 Loader 输出格式）

**涉及代码**:
- 新增 `StaticTaskLoader`（Plan/Check/Integrate）
- 新增 `RalphTaskLoader`（Build）
- `DEFAULT_STAGE_DEFINITIONS` 中的 Build `tasks: []` 占位

**调用关系**:
```
通用 Stage Runner
  ├── taskLoader.load(ctx, stageDef.tasks) → TaskDefinition[]
  │     ├── StaticTaskLoader: 从 baseTasks + ctx 组装 prompt
  │     └── RalphTaskLoader: 从 tasks.json 读取 + validate + sort
  │
  └── 按顺序 dispatch 到 Task Handler
```

**关键发现**:
- `StaticTaskLoader` 需要 Plan 的 prompt 组装逻辑（当前在 `PlanStageRunner.executeTasks()` 中）
- 这些 prompt 组装涉及 `issue.title`, `issue.body`, `worktreePath` 等上下文
- Loader 的职责："根据上下文准备可执行的 task 定义"

**隐藏依赖**:
- `RalphTaskLoader` 需要 `detectOpenSpecChange()` 来找到 `tasks.json` 路径
- 当前 `detectOpenSpecChange` 在 runner 里调用，统一后需要在 loader 里调用
- 但 `detectOpenSpecChange` 依赖 `worktreePath` 和 `issue`，这些在 `ctx` 中可用

**结论**: **依赖 #3**，但可以等 #3 的 Handler 接口确定后立即开始

---

### Issue #5: BaseStageRunner 改造为配置驱动

**前置条件**:
- **#3 Task Handler 提取**（runner 需要 dispatch 到 handler）
- **#4 Task Loader**（runner 需要加载 tasks）
- **#1 Fix Task 统一**（推荐，减少 `executeReportedTask` 的复杂度）

**涉及代码**:
- `workflow/base-stage-runner.ts` (731行) — 核心改造
- 各 runner 的 `executeTasks()` — 逐步移除

**当前模式（模板方法）**:
```typescript
abstract class BaseStageRunner {
  async run(ctx) {
    // 1. 处理 aggregate 单 task/check
    // 2. 执行 Pre-Task Checks
    // 3. 【抽象】executeTasks() — 子类实现
    // 4. 执行 Post-Task Checks
    // 5. 处理失败/approval
  }
  protected abstract executeTasks(ctx): Promise<unknown>;
  protected abstract getChecks(): Check[];
}
```

**目标模式（配置驱动）**:
```typescript
class StageRunner {
  async run(stageDef, ctx) {
    // 1. 处理 aggregate 单 task/check（不变）
    // 2. 加载 tasks: loader.load(ctx, stageDef.tasks)
    // 3. 按顺序执行 tasks: handler.execute(task, ctx)
    // 4. 执行 checks（从 stageDef.checks 加载）
    // 5. 处理失败/approval（不变）
  }
}
```

**关键问题：渐进式改造还是大爆炸式？**

**方案 A（渐进式）— 推荐**:
```typescript
class BaseStageRunner {
  async run(ctx) {
    // ... 现有逻辑 ...
    
    // 新增：如果子类提供 StageDefinition，走配置驱动
    const def = this.getStageDefinition?.();
    if (def) {
      return this.runWithDefinition(def, ctx);
    }
    
    // 否则走旧的抽象方法
    return this.runLegacy(ctx);
  }
}
```

**方案 B（大爆炸式）**:
- 一次性重写 BaseStageRunner
- 所有子类同时改造
- 风险高

**测试影响**:
- `base-stage-runner.test.ts` (468行) 使用 `TestStageRunner extends BaseStageRunner`
- 改造后 `TestStageRunner` 需要提供 `StageDefinition` 而不是覆盖 `executeTasks`
- **测试改动大**

**结论**:
- **依赖 #3, #4**
- **推荐渐进式**：BaseStageRunner 同时支持两种模式（legacy 抽象方法 + 新配置驱动）
- 先让一个 stage（如 Integrate）走配置驱动验证，再逐步迁移其他 stage

---

### Issue #6: Check 配置化

**前置条件**:
- **#5 BaseStageRunner 支持配置驱动**（Check 需要从配置加载）

**涉及代码**:
- `workflow/checks/` 下 6 个 Check 类
- 各 runner 的 `getChecks()` 方法

**当前模式**:
```typescript
// PlanStageRunner
getChecks() {
  return [
    new ProposalCompleteCheck(),
    new SpecsCompleteCheck(),
    // ... 硬编码实例
  ];
}
```

**目标模式**:
```yaml
# StageDefinition
checks:
  - name: proposal-complete
    handler: artifact-exists
    inputs: { path: '...' }
```

**关键发现**:
- Check 已经是干净的 `Check` 接口（`name` + `run(ctx)`）
- 只是注册方式不同（代码实例化 vs 配置驱动）
- Check Handler 注册表和 Task Handler 注册表类似

**隐藏依赖**:
- `HealthGateCheck` 需要 `HealthGatePolicy`（命令、超时等）
- 这些 policy 当前在 `workflow-loader.ts` 中定义
- 配置化后需要把 policy 也放到 `StageDefinition` 中

**结论**:
- **依赖 #5**
- 但改动量小，因为 Check 接口已经很干净
- 可以和 #5 同一个 PR 或紧随其后

---

### Issue #7: 统一为单 StageRunner

**前置条件**:
- **#5 BaseStageRunner 配置驱动**（所有 stage 都支持配置驱动）
- **#6 Check 配置化**（所有 check 都从配置加载）

**涉及代码**:
- 删除 `PlanStageRunner.ts`, `BuildStageRunner.ts`, `CheckStageRunner.ts`, `IntegrateStageRunner.ts`
- `WorkflowEngine` 的 runner 注册从数组改为单例

**关键发现**:
- 这是"收获期"：前面的工作做完后，这一步只是删除代码
- 但删除代码前需要确保所有 stage 都稳定运行在新模式下

**测试影响**:
- `integrate-stage-runner.test.ts` (1413行) 直接测试 `IntegrateStageRunner`
- `check-stage-ordering.test.ts` 测试 Check stage 行为
- 这些测试需要改为测试通用 `StageRunner` + 对应 `StageDefinition`

**隐藏依赖**:
- 各 runner 除了 `executeTasks()` 和 `getChecks()`，还有：
  - `getPreTaskChecks()` — 默认空，可被覆盖
  - `getCheckFailurePolicies()` — 默认空
  - `isApprovalCheck()` — 默认 false
  - `beforeRecheckAfterFix()` — Check 特有
  - `executeReportedTask()` — Fix Task 特有
- 这些钩子都需要在 `StageDefinition` 中表达或用其他方式处理

**结论**:
- **依赖 #5, #6**
- 是"最后一步"，删除代码比写代码简单
- 但测试迁移需要仔细处理

---

### Issue #8: 事件通用化

**前置条件**:
- **#7 统一为单 StageRunner**（所有 stage 用同一套 emit 逻辑）

**涉及代码**:
- `services/event-bus.ts` — 删除 stage 特有事件定义
- 各 runner 的 emit 调用点
- Web UI 4 个 hooks + PipelineView

**当前事件（后端 emit）**:
```
Plan: plan_round_start, plan_round_complete, plan_session_update
Build: build_stage_started, build_tasks_snapshot, ralph_task_update, ralph_loop_progress
Check: check_started, check_update, check_suite_status_changed
Integrate: integration_started, integration_step_updated, integration_completed, integration_failed
```

**通用事件（目标）**:
```
stage_started, stage_completed, stage_failed
stage_task_update
stage_progress
stage_check_update, stage_check_complete
approval_requested, approval_resolved
```

**关键发现**:
- 后端改动小：只是改 event 名称和增加 `stage` 字段
- **Web UI 改动大**：4 个 hooks 需要按 `stage` 字段过滤
- `useTaskProgress.ts`, `useSessionTimeline.ts`, `useActivityCards.ts`, `useSSE.tsx`

**隐藏依赖**:
- `plan_session_update` 包含 `roundType`, `roundIndex` 等 Plan 特有字段
- `integration_step_updated` 包含 `step`, `summary` 等 Integrate 特有字段
- 通用化后这些字段需要合并到 `stage_task_update` 的 `output` 中

**结论**:
- **依赖 #7**
- 后端改动 1 天，Web UI 改动 2-3 天
- 可以和 #7 同一个 PR，或紧随其后

---

### Issue #9: 状态系统合并

**前置条件**:
- **#7 统一为单 StageRunner**（稳定运行后）
- 需要单独评估 legacy 模式是否可以退役

**涉及代码**:
- `workflow/checkpoint-manager.ts` — legacy 断点续传
- `db/stage-execution-repo.ts` — 执行历史归档
- `services/stage-state-service.ts` — Web UI 实时状态

**三套系统的对比**:

| 维度 | CheckpointManager | StageExecutionRepo | StageStateService |
|------|-------------------|--------------------|--------------------|
| 表 | pipeline_checkpoint | stage_executions | stage_states + stage_tasks + stage_checks |
| Key | issueNumber + stage | id (uuid) | issueId + stage |
| 消费者 | Legacy runner resume | 审计查询 | Web UI API |
| 数据格式 | JSON string[] | JSON 数组 | 关系型多表 |
| Aggregate 模式 | 不使用 | 使用 | 使用 |
| Legacy 模式 | 使用 | 使用 | 使用 |

**关键发现**:
- **CheckpointManager 和 StageStateService 数据等价**：
  - `checkpoint.completedSteps` === `stage_tasks.filter(t => t.status === 'completed').map(t => t.task_id)`
- 但 Key 不同：CheckpointManager 用 `issueNumber`，StageStateService 用 `issueId`
- **StageExecutionRepo 是快照归档**：一次 stage 执行一条记录，taskResults 和 checkResults 是 JSON 数组

**合并难度**:
- 需要统一 key（全部用 issueId）
- 需要数据库迁移（合并表或删除表）
- 需要确保 legacy 模式的断点续传不受影响
- **风险极高**

**结论**:
- **延后评估**
- 建议等 aggregate 模式稳定运行、legacy 模式使用率低时再考虑
- 可能需要先统一 key（issueNumber → issueId）作为前置步骤

---

## 二、依赖关系图（详细版）

```
#2 emitSafe ───────────────────────────────────────────────────────► 完全独立
     │                                                               （零依赖）
     ▼
#1 Fix Task ───────────────────────────────────────────────────────► 可独立
     │                                                               （但接入 runner 需要适配）
     │
     ├──► 定义 FixTaskHandler 接口 ───────────────────────────────► #3 可用
     │
     ▼
#3 Handler ────────────────────────────────────────────────────────► 核心前置
     │                                                               （定义 Handler 接口）
     ├──► AgentSessionTaskHandler
     ├──► ServiceCallTaskHandler
     └──► RalphTaskHandler（拆分 RalphExecutor）
          │
          └──► 测试文件更新（8个测试文件）
               ├── ralph-executor.test.ts
               ├── ralph-executor-logging.test.ts
               ├── ralph-executor-auto-skip.test.ts
               ├── build-workflowrun-tasks.test.ts
               ├── workflow-session-handling.test.ts
               ├── wip-commit.test.ts
               ├── pipeline-checkpoint.test.ts
               └── build-pipeline-observability.test.ts
     │
     ▼
#4 Loader ─────────────────────────────────────────────────────────► 依赖 #3
     │                                                               （需要 Handler 接口）
     ├──► StaticTaskLoader
     │     └── 需要 Plan prompt 组装逻辑（从 PlanStageRunner 提取）
     └──► RalphTaskLoader
           └── 需要 detectOpenSpecChange() + validateDependencies()
     │
     ▼
#5 Runner ─────────────────────────────────────────────────────────► 核心重构
     │                                                               （依赖 #3, #4）
     ├──► 渐进式改造：BaseStageRunner 同时支持两种模式
     │     ├── Legacy 模式：子类覆盖 executeTasks() / getChecks()
     │     └── 新配置模式：子类提供 getStageDefinition()
     │
     ├──► 先让一个 stage 走新配置模式验证（推荐 Integrate，最简单）
     ├──► 再迁移 Plan / Check
     └──► 最后迁移 Build（最复杂，涉及 Ralph）
     │
     ▼
#6 Check ──────────────────────────────────────────────────────────► 依赖 #5
     │                                                               （runner 支持配置后才能接入）
     └──► 6 个 Check 类 → CheckHandler 注册表
     │
     ▼
#7 统一 ───────────────────────────────────────────────────────────► 依赖 #5, #6
     │                                                               （所有 stage 稳定后删除子类）
     ├──► 删除 4 个 runner 文件（~1500行）
     ├──► WorkflowEngine 注册改为单例
     └──► 测试文件更新（integrate-stage-runner.test.ts 等）
     │
     ▼
#8 事件 ───────────────────────────────────────────────────────────► 依赖 #7
     │                                                               （runner 统一后才能统一 emit）
     ├──► 后端：改 event 名称，增加 stage 字段
     └──► Web UI：4 个 hooks 迁移
     │
     ▼
#9 状态 ───────────────────────────────────────────────────────────► 延后评估
          （需要 #7 稳定 + legacy 退役评估）
```

---

## 三、推荐的实施顺序

### 批次 1：零依赖，可并行（1-2 天）

| 顺序 | Issue | 说明 |
|------|-------|------|
| 1 | **#2 emitSafe 上移** | 完全独立，零风险，为后续减少重复代码 |
| 2 | **#1 Fix Task 提取**（路径A：渐进） | 提取 FixTaskHandler，当前 runner 通过适配器调用。不依赖 runner 改造。 |

**为什么 #1 和 #2 可以并行**:
- #2 是纯工具方法上移，不影响业务逻辑
- #1 是提取 FixTaskHandler，runner 里的调用点从"内联函数"改为"调用 Handler"
- 两者不互相依赖

### 批次 2：Handler 接口定义（2-3 天）

| 顺序 | Issue | 说明 |
|------|-------|------|
| 3 | **#3 Task Handler 接口定义** | 先定义 `TaskHandler` 接口，不实现。让 #1 和 #4 知道接口长什么样。 |
| 4 | **AgentSessionTaskHandler 提取** | Plan/Check 共用，session pool + 引用计数 |
| 5 | **ServiceCallTaskHandler 提取** | Integrate 用，最简单 |

**为什么先定义接口**:
- `TaskHandler.execute(config, ctx): Promise<TaskResult>` 的签名需要确定
- FixTaskHandler、StaticTaskLoader、RalphTaskLoader 都需要知道这个接口
- 接口确定后，各实现可以并行开发

### 批次 3：Ralph 拆分（2-3 天）

| 顺序 | Issue | 说明 |
|------|-------|------|
| 6 | **RalphTaskLoader 提取** | 从 RalphExecutor 拆出 `readTasks` + `validateDependencies` + `sortTasksByOrder` |
| 7 | **RalphTaskHandler 提取** | 从 RalphExecutor 拆出 `buildTaskContext` + `withSession` + retry |
| 8 | **测试迁移** | 更新 8 个 Ralph 相关测试 |

**为什么 Ralph 拆分是瓶颈**:
- RalphExecutor 1086行，是代码量最大的组件
- 8个测试文件直接依赖，测试迁移工作量大
- Build stage 的核心逻辑，拆分后需要充分验证

### 批次 4：Runner 渐进式改造（3-5 天）

| 顺序 | Issue | 说明 |
|------|-------|------|
| 9 | **BaseStageRunner 增加配置驱动分支** | 保留 legacy 模式，新增 `runWithDefinition()` |
| 10 | **Integrate 迁移到配置驱动** | 最简单，只有 3 个 service-call task + 1 个 health check |
| 11 | **Plan 迁移到配置驱动** | 5 个 agent-session task + 多个 checks |
| 12 | **Check 迁移到配置驱动** | 1 个 agent-session task + review/merge checks |
| 13 | **Build 迁移到配置驱动** | 最复杂，RalphTaskLoader + RalphTaskHandler |

**为什么渐进式**:
- 每个 stage 独立迁移，可以单独验证
- 一个 stage 出问题不影响其他 stage
- 可以从最简单的 Integrate 开始验证架构

### 批次 5：清理（1-2 天）

| 顺序 | Issue | 说明 |
|------|-------|------|
| 14 | **Check 配置化** | 从 runner 硬编码改为配置驱动 |
| 15 | **删除 legacy runner 子类** | 所有 stage 稳定后删除旧代码 |
| 16 | **统一为单 StageRunner** | WorkflowEngine 注册改为单例 |

### 批次 6：事件通用化（2-3 天）

| 顺序 | Issue | 说明 |
|------|-------|------|
| 17 | **#8 事件通用化** | 后端 + Web UI 一起改 |

---

## 四、风险最高的改动点

| 排名 | 改动点 | 风险原因 | 缓解措施 |
|------|--------|----------|----------|
| 1 | **RalphExecutor 拆分** | 8 个测试文件依赖，Build 核心逻辑 | 保持 `runRalphLoop` 函数作为兼容层，逐步迁移测试 |
| 2 | **BaseStageRunner 改造** | 468行测试依赖，双模式共存复杂 | 渐进式：保留 legacy 分支，新配置分支独立验证 |
| 3 | **事件通用化** | Web UI 4 个 hooks 需要同步改 | 后端保持向后兼容（同时 emit 新旧事件），Web UI 逐步迁移 |
| 4 | **Fix Task 提取** | 涉及 4 个 runner 的 `executeReportedTask` | 先提取 Handler，runner 里通过适配器调用，不一次性改完 |

---

## 五、最小化改动策略下的实施建议

基于用户偏好"最小化改动策略：不删除依赖、V1 代码和废弃业务方法"：

```
Phase 1（并行，低风险）:
  #2 emitSafe 上移
  #1 Fix Task 提取（保留现有调用点，新增 Handler 层）

Phase 2（串行，中风险）:
  #3 Handler 接口定义
  #3 AgentSession + ServiceCall Handler 提取
  #3 RalphTaskLoader + RalphTaskHandler 拆分（保留 RalphExecutor 作为兼容层）

Phase 3（渐进式，中风险）:
  #5 BaseStageRunner 增加配置驱动分支（保留 legacy 分支）
  #4 StaticTaskLoader + RalphTaskLoader
  #5 Integrate → Plan → Check → Build 逐个迁移

Phase 4（低风险）:
  #6 Check 配置化
  #7 删除 legacy 子类（当所有 stage 稳定后）

Phase 5（中风险）:
  #8 事件通用化（后端保持双发，Web UI 逐步迁移）

Phase 6（延后）:
  #9 状态系统合并（等 legacy 退役评估后）
```

**关键原则**：
1. **保留兼容层**：RalphExecutor 拆分时保留原函数签名，测试逐步迁移
2. **双模式共存**：BaseStageRunner 同时支持 legacy 和配置驱动，不强制迁移
3. **逐个 stage 验证**：每个 stage 独立迁移，出问题可快速回滚
4. **不删除旧代码**：直到新代码稳定运行后再删除
