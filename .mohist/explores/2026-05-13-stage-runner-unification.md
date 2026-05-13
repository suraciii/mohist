# Workflow Stage Runner 统一化探索 — 最终结论

**日期**: 2026-05-13
**主题**: 将 Plan/Build/Check/Integrate 四个 StageRunner 统一为通用 runner
**状态**: 探索完成 — 架构清晰，5个关键决策已确认

---

## 五个关键决策

| # | 决策 | 结论 |
|---|------|------|
| 1 | Task 失败行为 | **Task 失败 = Stage 终止**。不再执行后续 tasks。 |
| 2 | Task Loader 模式 | **统一走 Loader（选项C修正版）**。所有 stage 经过 loader，StaticTaskLoader（Plan/Check/Integrate）+ RalphTaskLoader（Build）。 |
| 3 | SSE 事件层级 | **两层事件**：Runner emit `stage_task_update` + `stage_progress`，Handler 内部不 emit 独立事件。`ralph_` 事件全部统一。 |
| 4 | dependsOn 范围 | **只在 RalphTaskLoader 内部**。Loader 输出线性数组，Runner 无脑顺序执行。 |
| 5 | onlyTaskId | **Runner 层面处理**。Aggregate workflow 模式下 runner 直接过滤 task 并执行，不传给 Loader 或 Handler。Task 顺序执行，无跳过。 |

---

## 目标架构

```
┌─────────────────────────────────────────────────────────────────────┐
│                    通用 Stage Runner                                 │
├─────────────────────────────────────────────────────────────────────┤
│                                                                      │
│  1. Load tasks                                                       │
│     const allTasks = await taskLoaderRegistry                       │
│       .resolve(stageDef.loader)                                     │
│       .load(ctx, stageDef.tasks);                                   │
│                                                                      │
│  2. Filter checkpoint                                                │
│     const completed = checkpointManager.getResumeSteps(...);        │
│     const tasks = allTasks.filter(t => !completed.includes(t.id));  │
│                                                                      │
│  3. Execute tasks sequentially                                       │
│     for (const task of tasks) {                                     │
│       const handler = taskHandlerRegistry.resolve(task.handler);    │
│       const result = await handler.execute(task, ctx);               │
│       if (result.status === 'completed') {                          │
│         checkpointManager.markStepComplete(...);                     │
│       } else {                                                       │
│         throw new StageFailedError(result);                          │
│       }                                                              │
│     }                                                                │
│                                                                      │
│  4. Run checks → fix tasks → rerun checks                            │
│  5. Approval (if required)                                           │
│  6. Hooks                                                            │
│                                                                      │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Task Loader 设计

所有 stage 统一走 loader，loader 的职责是"根据上下文准备可执行的 task 定义"。

```typescript
interface TaskLoader {
  load(ctx: StageContext, baseTasks: TaskDefinition[]): Promise<TaskDefinition[]>;
}
```

### StaticTaskLoader（Plan / Check / Integrate）

```typescript
class StaticTaskLoader implements TaskLoader {
  async load(ctx, baseTasks) {
    return baseTasks.map(task => ({
      ...task,
      inputs: {
        ...task.inputs,
        prompt: this.resolvePrompt(task.inputs.promptTemplate, ctx),
      },
    }));
  }
}
```

**为什么 prompt 组装在 loader 里**：Prompt 组装依赖 stage 上下文（issue、worktreePath 等），Handler 应该接收"可直接执行"的 inputs，不做业务逻辑。

### RalphTaskLoader（Build）

```typescript
class RalphTaskLoader implements TaskLoader {
  async load(ctx, baseTasks) {
    const tasks = readTasks(ctx.change.tasksPath) ?? [];
    
    // 验证依赖
    const validation = validateTaskDependencies(tasks);
    if (!validation.valid) throw new Error(...);
    
    // 拓扑排序（loader 内部完成，runner 不感知依赖图）
    const sorted = sortTasksByOrder(tasks);
    
    // 输出线性数组
    return sorted.map(t => ({
      id: t.id,
      handler: 'ralph-task',
      inputs: { taskId: t.id, title: t.title },
    }));
  }
}
```

**关键**：`dependsOn` 只在 tasks.json 和 loader 内部存在，解析后输出的是线性数组，Runner 无脑顺序执行。

---

## Task Handler 设计

```typescript
interface TaskHandler {
  execute(config: TaskConfig, ctx: StageContext): Promise<TaskResult>;
}
```

### AgentSessionTaskHandler（Plan / Check）

```typescript
class AgentSessionTaskHandler implements TaskHandler {
  private sessions = new Map<string, AgentSession>();
  
  async execute(config, ctx) {
    let session = this.sessions.get(config.session);
    if (!session) {
      session = await AgentSession.create({ cwd: ctx.worktreePath, ... });
      this.sessions.set(config.session, session);
    }
    session.refCount++;
    try {
      const result = await session.execute(config.inputs.prompt);
      return { status: result.success ? 'completed' : 'failed', output: result.text };
    } finally {
      session.refCount--;
      if (session.refCount === 0) {
        await session.close();
        this.sessions.delete(config.session);
      }
    }
  }
}
```

**Session 复用**：通过 `session` 标识符 + 引用计数自动管理生命周期，不需要显式 `closeSession` 标记。

### RalphTaskHandler（Build）

```typescript
class RalphTaskHandler implements TaskHandler {
  async execute(config, ctx) {
    const { taskId, title } = config.inputs;
    let learnings = loadLearningsFromDir(ctx.change.sessionMemoriesPath);
    
    for (let attempt = 1; attempt <= maxRetries; attempt++) {
      const prompt = buildTaskContext({
        change: ctx.change,
        task: { id: taskId, title },
        learnings,
        isRetry: attempt > 1,
      }).fullPrompt;
      
      const result = await withSession({ cwd: ctx.worktreePath, task: prompt, taskId, ... });
      
      if (result.success) {
        return { status: 'completed', output: result.text };
      }
      
      const category = categorizeFailure(result.error);
      if (!FAILURE_CATEGORY_CONFIGS[category].retryable || attempt >= maxRetries) {
        return { status: 'failed', error: result.error };
      }
      
      // 重试：存储 learning，下一次循环会读取
      await storeFailureLearning(ctx.change, { id: taskId }, result.error);
      learnings = loadLearningsFromDir(ctx.change.sessionMemoriesPath);
    }
    
    return { status: 'failed', error: 'Max retries exceeded' };
  }
}
```

**关键**：
- RalphTaskHandler **只执行一个 task**（由 runner 逐个调度），不需要遍历 task 列表
- Ralph 的内部重试（最多3次）对 runner 不可见，runner 只看到最终结果（completed/failed）
- `learnings` 通过文件系统共享，不需要 session 复用
- 每次 attempt 失败后更新 learning，下一次 attempt 的 prompt 包含失败原因

### ServiceCallTaskHandler（Integrate）

```typescript
class ServiceCallTaskHandler implements TaskHandler {
  async execute(config, ctx) {
    const { service, method } = config.inputs;
    const serviceInstance = ctx.services[service];
    const result = await serviceInstance[method](ctx);
    return { status: 'completed', output: result };
  }
}
```

---

## 各 Stage 的完整配置

```yaml
# Plan
stage: Plan
loader: static
tasks:
  - id: proposal
    handler: agent-session
    session: plan-session
    inputs: { promptTemplate: 'proposal', artifact: 'proposal.md' }
  - id: specs
    handler: agent-session
    session: plan-session
    inputs: { promptTemplate: 'specs', artifact: 'specs/' }
  - id: design
    handler: agent-session
    session: plan-session
    inputs: { promptTemplate: 'design', artifact: 'design.md' }
  - id: tasks
    handler: agent-session
    session: plan-session
    inputs: { promptTemplate: 'tasks', artifact: 'tasks.json' }
  - id: self-review
    handler: agent-session
    session: plan-session
    inputs: { promptTemplate: 'self-review', artifact: 'self-review.md' }
checks:
  - name: proposal-complete
  - name: specs-complete
  - name: design-complete
  - name: tasks-valid
  - name: self-review-passed
  - name: health:plan
requiresApproval: true
approvalCheckName: user-approval
checkFailurePolicies:
  - checkName: self-review-passed
    fixTaskId: fix-plan-review

# Build
stage: Build
loader: ralph
tasks: []  # 由 RalphTaskLoader 从 tasks.json 填充
checks:
  - name: health:build
checkFailurePolicies:
  - checkName: health:build
    fixTaskId: fix-build-health

# Check
stage: Check
loader: static
tasks:
  - id: ai-review
    handler: agent-session
    inputs: { promptTemplate: 'review', artifact: 'review.md' }
checks:
  - name: review-passed
  - name: merge-ready
requiresApproval: true
approvalCheckName: user-approval
checkFailurePolicies:
  - checkName: review-passed
    fixTaskId: fix-review-findings
  - checkName: merge-ready
    fixTaskId: fix-merge-readiness

# Integrate
stage: Integrate
loader: static
tasks:
  - id: spec-sync
    handler: service-call
    inputs: { service: 'OpenSpecIntegrator', method: 'apply' }
  - id: archive-change
    handler: service-call
    inputs: { service: 'ArtifactManager', method: 'archiveChange' }
  - id: merge
    handler: service-call
    inputs: { service: 'WorktreeManager', method: 'mergeApprovedCandidate' }
checks:
  - name: health:integrate
checkFailurePolicies:
  - checkName: health:integrate
    fixTaskId: fix-integrate-health
```

---

## Aggregate 模式：单 Task 调度的真实场景

Aggregate workflow 不是"一次跑完整个 stage"，而是 **aggregate 状态机逐个调度 tasks**：

```
WorkflowEngine.runAggregateWorkflow()
├── resumeDecision() → nextWork = { kind: 'task', stage: 'Build', taskId: 'T-001' }
├── runner.run(ctx with requestedWork=T-001)
│   └── BaseStageRunner.run() 第126行
│       └── executeTaskWork(ctx, 'T-001')  ← 只执行这一个 task
├── completeTask(T-001, result) 上报 aggregate
├── resumeDecision() → nextWork = { kind: 'task', stage: 'Build', taskId: 'T-002' }
├── runner.run(ctx with requestedWork=T-002)
│   └── executeTaskWork(ctx, 'T-002')
... 循环直到所有 tasks 完成
```

**这就是"单 task 执行"的真实场景** —— 不是用户手动选 task，而是 aggregate 状态机逐个调度。每次 runner 被调用只执行一个 task，完成后把结果上报给 aggregate，aggregate 再决定下一个 work。

`WorkflowRun.nextWork()` 的逻辑：
```typescript
nextWork():
  if (status === 'passed') return { kind: 'complete' }
  if (status === 'failed') return { kind: 'failed', reason }
  stageRun = currentStageRun()
  if (stageRun.status === 'awaiting-approval') return { kind: 'await-approval' }
  task = stageRun.nextTask()        // ← 获取下一个未完成的 task
  if (task) return { kind: 'task', taskId: task.id }
  check = stageRun.nextCheck()      // ← 所有 task 完成后，获取下一个 check
  if (check) return { kind: 'check', checkName: check.name }
  return { kind: 'complete' }
```

**统一 runner 下的处理方式**：

```typescript
async run(stageDef, options) {
  // Aggregate 模式：只执行一个 task
  if (options?.requestedWork?.kind === 'task') {
    const allTasks = await taskLoader.load(ctx, stageDef.tasks);
    const task = allTasks.find(t => t.id === options.requestedWork.taskId);
    const handler = taskHandlerRegistry.resolve(task.handler);
    return handler.execute(task, ctx);
  }
  
  // ... 完整 stage 流程（legacy 模式）
}
```

**关键**：`onlyTaskId` 不需要传给 Task Loader 或 Handler。它是 runner 层面的 aggregate 调度逻辑。

---

## Event 层级设计（`ralph_` 事件统一化）

### 调查发现

当前 `ralph_task_update` 被 Web UI 的 **4 个 hooks** 消费：
- `useTaskProgress.ts` — 更新 task 列表 pass/fail 状态 + build-status progress
- `useSessionTimeline.ts` — 更新 task progress map
- `useActivityCards.ts` — 更新卡片 task progress
- `useSSE.tsx` — SSE 转发

`ralph_loop_progress` 被 **3 个 hooks** 消费：
- `useTaskProgress.ts` — 更新 build-status progress
- `useSessionTimeline.ts` — 更新 loop progress
- `useActivityCards.ts` — 更新卡片 task progress

同时 `stage_task_update` **已经存在**且被 PipelineView 使用：
```typescript
// event-bus.ts 第56行
stage_task_update: { 
  issueId, projectId, stage, taskId, taskTitle, 
  status: 'started' | 'completed' | 'failed' | 'retrying', 
  attempt, artifacts 
}
```

### 统一方案

| 当前事件 | 统一后事件 | 说明 |
|----------|-----------|------|
| `ralph_task_update` | `stage_task_update` | 字段微调，增加 `stage` 字段 |
| `ralph_loop_progress` | `stage_progress` | 新增，runner 层面计算 |
| `build_stage_started` | `stage_started` | 已存在 |
| `build_stage_completed` | `stage_completed` | 已存在 |
| `build_stage_failed` | `stage_failed` | 已存在 |
| `build_tasks_snapshot` | 移除 | 由 loader 输出 + `stage_task_update` 替代 |

**`stage_task_update` 已经支持 `retrying` 状态**，所以 Ralph 的 attempt-level 状态可以直接用这个事件 emit。

### 统一后的事件体系

| 事件 | Emitter | 触发时机 |
|------|---------|----------|
| `stage_started` | Runner | Stage 开始执行 |
| `stage_task_update` | Runner | 每个 task 开始/完成/失败时 |
| `stage_progress` | Runner | 每个 task 完成后（计算 completed/failed/total） |
| `stage_check_update` | Runner | 每个 check 完成后 |
| `stage_completed` / `stage_failed` | Runner | Stage 结束 |

**Runner 内统一 emit**：

```typescript
for (const task of tasks) {
  eventBus.emit('stage_task_update', {
    stage: stageDef.stage,
    taskId: task.id,
    taskTitle: task.title,
    status: 'started',
    attempt: 1,
    artifacts: [],
  });
  
  const result = await handler.execute(task, ctx);
  
  eventBus.emit('stage_task_update', {
    stage: stageDef.stage,
    taskId: task.id,
    taskTitle: task.title,
    status: result.status,  // 'completed' | 'failed' | 'retrying'
    attempt: result.attempts ?? 1,
    artifacts: result.artifacts ?? [],
  });
  
  if (result.status === 'completed') completed++;
  if (result.status === 'failed') failed++;
  
  eventBus.emit('stage_progress', {
    stage: stageDef.stage,
    completed,
    failed,
    total: tasks.length,
    pending: tasks.length - completed - failed,
  });
}
```

**Web UI 迁移**：

```typescript
// useTaskProgress.ts 统一后
onAgentEvent('stage_task_update', (event) => {
  if (event.stage !== 'build') return;  // 只关心 build stage
  // 更新 task 列表...
});

onAgentEvent('stage_progress', (event) => {
  if (event.stage !== 'build') return;
  // 更新 progress...
});
```

---

## Checkpoint / Resume 统一

```typescript
// Runner 统一处理
async run(stageDef, options) {
  const allTasks = await taskLoader.load(ctx, stageDef.tasks);
  
  // 过滤已完成的
  const completedIds = checkpointManager.getResumeSteps(issue.number, stageDef.stage);
  const tasks = allTasks.filter(t => !completedIds.includes(t.id));
  
  // 执行
  for (const task of tasks) {
    const result = await handler.execute(task, ctx);
    if (result.status === 'completed') {
      checkpointManager.markStepComplete(issue.number, stageDef.stage, task.id);
    } else {
      throw new StageFailedError(result);
    }
  }
}
```

**兼容性**：
- Plan 的 taskId 就是 artifact 类型（'proposal', 'specs'...），当前已经在用这个格式 mark checkpoint
- Build 的 taskId 是 'T-001', 'T-002'...，和 checkpoint 格式兼容
- Check/Integrate 通常一次性执行，无 checkpoint 问题

---

## 代码拆分映射

| 当前代码 | 拆分到 |
|---------|--------|
| `PlanStageRunner.executeTasks()` (5次AgentSession调用) | `StaticTaskLoader` (prompt组装) + `AgentSessionTaskHandler` |
| `BuildStageRunner.executeTasks()` (RalphExecutor调用) | `RalphTaskLoader` + `RalphTaskHandler` |
| `CheckStageRunner.executeTasks()` (AI review) | `StaticTaskLoader` + `AgentSessionTaskHandler` |
| `IntegrateStageRunner.executeTasks()` (3个service调用) | `StaticTaskLoader` + `ServiceCallTaskHandler` |
| `RalphExecutor.runRalphLoop()` (1086行) | `RalphTaskLoader` (~50行) + `RalphTaskHandler` (~150行) + Runner增强(~100行) |
| `BaseStageRunner` (模板方法模式) | 通用 `StageRunner` (配置驱动 dispatch) |

**Ralph 拆分详解**：
- `RalphTaskLoader`：从 `tasks.json` 读取 + `validateDependencies` + `sortTasksByOrder` → 输出线性 task 数组
- `RalphTaskHandler`：`buildTaskContext` + `withSession` + 失败分类重试 + `storeFailureLearning` → 只执行**一个** task（runner 逐个调度）
- 原 `RalphExecutor` 的 while 循环和 `findNextPendingTask` 被 runner 接管

---

## 演进路径

```
Phase 1: 提取 Task Handler（不改变 runner 结构）
         - AgentSessionTaskHandler（含 session pool）
         - RalphTaskHandler（从 RalphExecutor 拆分）
         - ServiceCallTaskHandler
         → 验证"task 化"的可行性

Phase 2: 引入 Task Loader + 改造 BaseStageRunner
         - StaticTaskLoader / RalphTaskLoader
         - BaseStageRunner 支持配置驱动 dispatch
         - 子类从"实现 executeTasks"改为"注册 handlers"
         → runner 趋同

Phase 3: 统一为单个 StageRunner
         - 移除所有子类（Plan/Build/Check/Integrate）
         - stage 差异完全由 StageDefinition 决定
         → 新增 stage = 新增配置
```

---

## 新增 Stage 的成本对比

| | 当前架构 | 统一后 |
|---|---|---|
| 修改文件数 | ~7 个文件（runner/类型/领域/配置/状态服务/注册） | 1 个（StageDefinition 配置） |
| 新增代码 | 200~400 行 runner + 散落改动 | ~20 行配置 |
| 认知负担 | 需要理解 runner 继承体系 | 只需理解 Task Handler 接口 |

---

## 参考资料

- GitHub Actions Runner: `src/Runner.Worker/StepsRunner.cs`
- GitHub Actions Runner: `src/Runner.Worker/JobRunner.cs`
- Azure DevOps Agent: `src/Agent.Worker/StepsRunner.cs`
- Azure DevOps Agent: `src/Agent.Worker/JobRunner.cs`
- Mohist: `packages/cli/src/workflow/base-stage-runner.ts`
- Mohist: `packages/cli/src/workflow/domain/index.ts`
- Mohist: `packages/cli/src/workflow/build-stage-runner.ts`
- Mohist: `packages/cli/src/openspec/ralph-executor.ts`
- Mohist: `packages/cli/src/agent-runtime/agent-session.ts`
