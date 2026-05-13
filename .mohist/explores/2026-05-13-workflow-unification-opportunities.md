# Workflow 可统一化设计点 — 详细分析

**日期**: 2026-05-13
**基础**: Stage Runner 统一化探索结论
**扩展**: 5 个可统一化方向的深入分析

---

## 一、Fix Task 统一

### 当前现状

3 个独立的 fix task 实现，每个都是一个独立的 TypeScript 文件：

| Fix Task | 文件 | 大小 | 用途 |
|----------|------|------|------|
| `runHealthFixTask` | `health-fix-task.ts` | 195 行 | fix-plan/build/check/integrate-health |
| `runReviewFixTask` | `review-fix-task.ts` | 182 行 | fix-review-findings |
| `runPlanRepairTask` | `plan-repair-task.ts` | 271 行 | repair-plan-artifacts |

**核心模式完全一致**（以 health-fix-task 为例）：

```typescript
export async function runHealthFixTask(ctx, options) {
  // 1. emit stage_task_update('started')
  emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ..., 'started', ...);
  
  // 2. 创建 observers
  const observers = createWorkflowSessionObservers({ eventBus, workflowLogRepo, ... });
  
  // 3. 组装 acpOptions
  const acpOptions = { cwd, issueId, projectId, issueNumber, observers, ... };
  
  // 4. 组装 prompt（这是唯一的差异点）
  const prompt = buildHealthFixPrompt(ctx, options);
  
  // 5. 创建 session + execute
  const session = await AgentSession.create(acpOptions);
  const result = await session.execute(prompt, { kind: 'recovery', title });
  
  // 6. emit stage_task_update('completed'/'failed')
  emitStageTaskUpdate(ctx.eventBus, ctx.issue.id, ..., result.success ? 'completed' : 'failed', ...);
  
  // 7. 返回 StageTaskResult
  return { taskId, title, status, artifacts: [], attempts, duration, reason, causedBy };
}
```

### Prompt 组装差异对比

```typescript
// health-fix-task.ts
function buildHealthFixPrompt(ctx, options) {
  return [
    `## Health Gate Fix Required`,
    `Issue #${ctx.issue.number}: ${ctx.issue.title}`,
    `Failed check: ${options.failedCheck.name}`,
    `Health command: ${options.healthCommand}`,
    `## Failure Summary`, options.failedCheck.message,
    `## Check Output`, JSON.stringify(options.failedCheck.output),
    `## Instructions`, ...
  ].join('\n');
}

// review-fix-task.ts
function buildReviewFixPrompt(ctx, options) {
  const output = options.failedCheck.output as { verdict, reviewReport, fixSuggestions };
  return [
    `## Review Fix Required`,
    `Issue #${ctx.issue.number}: ${ctx.issue.title}`,
    `Failed check: ${options.failedCheck.name}`,
    `## Review Report`, output.reviewReport,
    `## Fix Suggestions`, output.fixSuggestions,
    `## Instructions`, ...
  ].join('\n');
}

// plan-repair-task.ts
function buildRepairPrompt(ctx, options) {
  const missing = detectMissingArtifacts(changeDir);  // ← 从文件系统检测
  return [
    `## Plan Artifact Repair Required`,
    `Issue #${ctx.issue.number}: ${ctx.issue.title}`,
    `Failed check: ${options.failedCheck.name}`,
    `## Missing or Invalid Artifacts`, ...missing.map(m => `- ${m}`),
    `## Instructions`, ...
  ].join('\n');
}
```

**差异点只有 prompt 组装逻辑**，执行骨架完全相同。

### 当前在 Stage Runner 中的注册

每个 stage runner 的 `executeReportedTask` 里硬编码：

```typescript
// PlanStageRunner
if (taskId === 'repair-plan-artifacts') return runPlanRepairTask(ctx, {...});
if (taskId === 'fix-plan-health') return runHealthFixTask(ctx, {...});

// BuildStageRunner
if (taskId === 'fix-build-health') return runHealthFixTask(ctx, {...});

// CheckStageRunner
if (taskId === 'fix-review-findings' && failedCheck?.name === 'review-passed') 
  return runReviewFixTask(ctx, {...});
if (taskId === 'fix-merge-readiness' && failedCheck?.name === 'merge-ready')
  return ...;  // 内联实现

// IntegrateStageRunner
if (taskId === 'fix-integrate-health') return runHealthFixTask(ctx, {...});
```

### 统一方案

Fix Task 本质就是 **agent-session task**，和 Plan/Check 阶段的普通 task 没有区别。差异只在 prompt 组装。

**方案：Prompt Builder 注册表**

```typescript
// 统一的 Fix Task Handler
class FixTaskHandler implements TaskHandler {
  private promptBuilders = new Map<string, PromptBuilder>();
  
  register(builder: PromptBuilder) {
    this.promptBuilders.set(builder.name, builder);
  }
  
  async execute(config, ctx) {
    const builder = this.promptBuilders.get(config.inputs.promptBuilder);
    if (!builder) throw new Error(`Unknown prompt builder: ${config.inputs.promptBuilder}`);
    
    const prompt = builder.build(ctx, config.inputs);
    
    // 复用 AgentSessionTaskHandler 的执行逻辑
    // 或直接用 withSession
    const result = await withSession({
      cwd: ctx.worktreePath,
      task: prompt,
      taskId: config.id,
      observers: createWorkflowSessionObservers({...}),
    });
    
    return { status: result.success ? 'completed' : 'failed', ... };
  }
}

// Prompt Builder 接口
interface PromptBuilder {
  name: string;
  build(ctx: StageContext, inputs: unknown): string;
}

// 3 个 prompt builder 实现
class HealthFixPromptBuilder implements PromptBuilder {
  name = 'health-fix';
  build(ctx, inputs) {
    const { failedCheck, healthCommand } = inputs;
    return [/* 原来的 prompt 组装逻辑 */].join('\n');
  }
}

class ReviewFixPromptBuilder implements PromptBuilder {
  name = 'review-fix';
  build(ctx, inputs) {
    const { failedCheck } = inputs;
    const output = failedCheck.output;
    return [/* 原来的 prompt 组装逻辑 */].join('\n');
  }
}

class PlanRepairPromptBuilder implements PromptBuilder {
  name = 'plan-repair';
  build(ctx, inputs) {
    const missing = detectMissingArtifacts(ctx.changeDir);
    return [/* 原来的 prompt 组装逻辑 */].join('\n');
  }
}
```

**配置化注册**：

```yaml
checkFailurePolicies:
  - checkName: health:build
    fixTask:
      id: fix-build-health
      handler: agent-session
      inputs:
        promptBuilder: health-fix
        healthCommand: 'npm run build'
        
  - checkName: review-passed
    fixTask:
      id: fix-review-findings
      handler: agent-session
      inputs:
        promptBuilder: review-fix
        
  - checkName: self-review-passed
    fixTask:
      id: repair-plan-artifacts
      handler: agent-session
      inputs:
        promptBuilder: plan-repair
```

**改动量**：
- 新增 `FixTaskHandler`（~50 行）
- 提取 3 个 `PromptBuilder`（各 ~30 行）
- 删除 `health-fix-task.ts`（195 行）
- 删除 `review-fix-task.ts`（182 行）
- 删除 `plan-repair-task.ts`（271 行）
- 各 stage runner 删除 `executeReportedTask` 里的 fix task 分支

**净效果**：-400 行代码，+150 行统一代码 = **-250 行**

---

## 二、Check 配置化

### 当前现状

Check 已经是一个干净的接口：

```typescript
// checks/index.ts
export interface Check {
  name: string;
  run(ctx: CheckContext): Promise<CheckResult>;
}
```

但每个 stage runner 硬编码注册：

```typescript
// PlanStageRunner.getChecks()
return [
  new ProposalCompleteCheck(),      // 检查 proposal.md 存在
  new SpecsCompleteCheck(),         // 检查 specs/ 存在
  new DesignCompleteCheck(),        // 检查 design.md 存在
  new TasksValidCheck(),            // 检查 tasks.json 有效
  new SelfReviewPassedCheck(),      // 检查 self-review.md
  new HealthGateCheck({ command: 'npm run typecheck' }),
  new UserApprovalCheck(Stage.Plan),
];

// BuildStageRunner.getChecks()
return [
  new HealthGateCheck({ command: 'npm run build' }),
];

// CheckStageRunner.getChecks()
return [
  new ReviewPassedCheck(),          // 解析 review.md verdict
  new MergeReadyCheck(),            // 检查 git merge 可行性
  new UserApprovalCheck(Stage.Check),
];

// IntegrateStageRunner.getChecks()
return [
  new HealthGateCheck({ command: 'npm run build' }),  // post-merge health
];
```

### 分析：Check 的两种类型

| 类型 | 代表 | 特点 |
|------|------|------|
| **Artifact Check** | ProposalComplete, SpecsComplete, DesignComplete, TasksValid, SelfReviewPassed, ReviewPassed | 检查文件/artifacts 的存在和内容 |
| **Health Gate** | HealthGateCheck | 执行命令（npm run build/test/typecheck） |
| **Merge Check** | MergeReadyCheck | 检查 git 状态 |
| **Approval Check** | UserApprovalCheck | 检查 issue.approval 状态 |

### 统一方案

```yaml
checks:
  # Plan
  - name: proposal-complete
    handler: artifact-exists
    inputs: { path: 'openspec/changes/{slug}/proposal.md', minSize: 1 }
  - name: specs-complete
    handler: artifact-exists
    inputs: { path: 'openspec/changes/{slug}/specs', isDir: true }
  - name: design-complete
    handler: artifact-exists
    inputs: { path: 'openspec/changes/{slug}/design.md', minSize: 1 }
  - name: tasks-valid
    handler: json-valid
    inputs: { path: 'openspec/changes/{slug}/tasks.json', schema: 'tasks-schema' }
  - name: self-review-passed
    handler: artifact-exists
    inputs: { path: 'openspec/changes/{slug}/self-review.md', minSize: 1 }
  - name: health:plan
    handler: health-gate
    inputs: { command: 'npm run typecheck', timeout: 300000 }
  - name: user-approval
    handler: user-approval
    
  # Check
  - name: review-passed
    handler: markdown-verdict
    inputs: { path: 'openspec/changes/{slug}/review.md', verdictField: 'verdict' }
  - name: merge-ready
    handler: merge-ready
  - name: user-approval
    handler: user-approval
    
  # Build / Integrate
  - name: health:build
    handler: health-gate
    inputs: { command: 'npm run build', timeout: 300000 }
  - name: health:integrate
    handler: health-gate
    inputs: { command: 'npm run build', timeout: 300000 }
```

**Check Handler 注册表**：

```typescript
const checkHandlers = new Map<string, CheckHandler>([
  ['artifact-exists', new ArtifactExistsCheckHandler()],
  ['json-valid', new JsonValidCheckHandler()],
  ['health-gate', new HealthGateCheckHandler()],
  ['markdown-verdict', new MarkdownVerdictCheckHandler()],
  ['merge-ready', new MergeReadyCheckHandler()],
  ['user-approval', new UserApprovalCheckHandler()],
]);
```

**改动量**：
- 6 个 Check 类合并为 6 个 CheckHandler（结构不变，注册方式改变）
- 删除各 runner 的 `getChecks()` 方法
- `StageDefinition` 增加 `checks` 配置

**净效果**：代码量持平，但**新增 stage 不需要写 runner 代码**，只需要配置 check。

---

## 三、事件通用化

### 当前现状

Stage 特有事件（EventBus 定义）：

```typescript
type EventMap = {
  // Build 特有
  build_stage_started: { issueId, projectId, stage: 'build', changePath, tasksCount, timestamp };
  build_tasks_snapshot: { issueId, projectId, total, pending, passed };
  build_stage_completed: { issueId, projectId, completed, failed, total, duration, timestamp };
  build_stage_failed: { issueId, projectId, reason, details, timestamp };
  
  // Check 特有
  check_started: { issueId, projectId, issueNumber };
  check_update: { issueId, projectId, checkName, status, duration, autoFixed, verdict, snapshotSha };
  check_suite_status_changed: { issueId, projectId, issueNumber, suiteStatus, snapshotSha };
  
  // Plan 特有
  plan_round_start: { issueId, projectId, roundType, roundLabel, roundIndex, acpSessionId, coderSessionId };
  plan_round_complete: { issueId, projectId, roundType, roundLabel, roundIndex, duration, verdict };
  plan_session_update: { issueId, projectId, roundType, roundIndex, sessionUpdate, data, acpSessionId };
  
  // Integrate 特有
  integration_started: { issueId, projectId, issueNumber };
  integration_step_updated: { issueId, projectId, issueNumber, step, status, summary, output };
  integration_completed: { issueId, projectId, issueNumber, steps };
  integration_failed: { issueId, projectId, issueNumber, failingStep, error, output };
  
  // Ralph 特有（已在 Stage Runner 统一化中讨论）
  ralph_task_update: { ... };
  ralph_loop_progress: { ... };
};
```

### Web UI 消费分析

```typescript
// useTaskProgress.ts
onAgentEvent('ralph_task_update', ...)      // Build task 进度
onAgentEvent('ralph_loop_progress', ...)    // Build loop 进度

// useSessionTimeline.ts
onAgentEvent('ralph_task_update', ...)      // Build task 进度（timeline 视图）
onAgentEvent('ralph_loop_progress', ...)    // Build loop 进度
onAgentEvent('plan_round_start', ...)       // Plan round 开始
onAgentEvent('plan_round_complete', ...)    // Plan round 完成
onAgentEvent('plan_session_update', ...)    // Plan session 事件

// useActivityCards.ts
onAgentEvent('ralph_task_update', ...)      // 卡片 task 进度
onAgentEvent('ralph_loop_progress', ...)    // 卡片 loop 进度
onAgentEvent('build_stage_started', ...)    // 卡片 stage 状态
onAgentEvent('build_stage_completed', ...)  // 卡片 stage 状态

// PipelineView.tsx
onAgentEvent('stage_task_update', ...)      // Pipeline task 状态（通用）
```

### 统一方案

**核心原则**：所有 stage 用同一套事件，Web UI 按 `stage` 字段过滤。

| 当前事件 | 统一后 | 说明 |
|----------|--------|------|
| `build_stage_started` | `stage_started` | 增加 `stage` 字段 |
| `build_stage_completed` | `stage_completed` | 增加 `stage` 字段 |
| `build_stage_failed` | `stage_failed` | 增加 `stage` 字段 |
| `build_tasks_snapshot` | `stage_task_update` x N | loader 加载的每个 task emit 一个 |
| `check_started` | `stage_started` | 同上 |
| `check_update` | `stage_check_update` | 已有通用事件 |
| `check_suite_status_changed` | `stage_check_complete` | 新增 |
| `plan_round_start` | `stage_task_update` (task=proposal) | Plan 的每个 artifact 就是一个 task |
| `plan_round_complete` | `stage_task_update` (task=self-review) | 最后一个 task 完成 |
| `plan_session_update` | `coder_text_chunk` / `coder_tool_call` | 已存在的通用事件 |
| `integration_started` | `stage_started` | 同上 |
| `integration_step_updated` | `stage_task_update` | Integrate 的每个 step 就是一个 task |
| `integration_completed` | `stage_completed` | 同上 |
| `integration_failed` | `stage_failed` | 同上 |

**统一后的事件体系（精简到 8 个）**：

```typescript
type EventMap = {
  // Stage 生命周期
  stage_started: { issueId, projectId, stage, timestamp };
  stage_completed: { issueId, projectId, stage, timestamp };
  stage_failed: { issueId, projectId, stage, reason, timestamp };
  
  // Task 进度
  stage_task_update: { issueId, projectId, stage, taskId, taskTitle, status, attempt, artifacts };
  stage_progress: { issueId, projectId, stage, completed, failed, total, pending };
  
  // Check 进度
  stage_check_update: { issueId, projectId, stage, checkName, status, message, output };
  stage_check_complete: { issueId, projectId, stage, allPassed, snapshotSha };
  
  // Approval
  approval_requested: { issueId, projectId, stage };
  approval_resolved: { issueId, projectId, stage, approved };
};
```

**Web UI 迁移**：

```typescript
// 之前：监听 build_stage_started
onAgentEvent('build_stage_started', (event) => {
  // 只处理 build
});

// 之后：监听 stage_started，按 stage 过滤
onAgentEvent('stage_started', (event) => {
  if (event.stage !== 'build') return;
  // 处理 build
});

// 之前：监听 plan_round_start
onAgentEvent('plan_round_start', (event) => {
  updateTimeline({ roundType: event.roundType, ... });
});

// 之后：监听 stage_task_update
onAgentEvent('stage_task_update', (event) => {
  if (event.stage !== 'plan') return;
  updateTimeline({ taskId: event.taskId, ... });
});
```

**风险**：
- Web UI 改动面大（4 个 hooks + PipelineView）
- 第三方消费这些事件的代码可能受影响
- 事件字段可能需要合并（如 `plan_round_start` 有 `roundIndex`，`stage_task_update` 没有）

**建议**：**Phase 3 再做**，因为 Web UI 改动面大且收益主要是"一致性"而非"功能"。

---

## 四、emitSafe / writeLog 上移

### 当前现状

每个 stage runner 都复制了这两个私有方法：

```typescript
// BuildStageRunner
private emitSafe(eventBus, event, data) {
  if (!eventBus) return;
  try { eventBus.emit(event, data); } catch (e) { log.warn(...); }
}
private writeLog(workflowLogRepo, issueId, eventType, data) {
  if (!workflowLogRepo) return;
  try { workflowLogRepo.insert(issueId, null, eventType, data); } catch (e) { log.warn(...); }
}

// PlanStageRunner 也有（名字可能不同但逻辑一样）
```

### 统一方案

**上移到 StageContext**：

```typescript
interface StageContext {
  // ... 现有字段
  
  // 新增：安全的 emit 和 log 方法
  emit(event: string, data: object): void;
  log(eventType: string, data: object): void;
}

// WorkflowEngine.buildContext() 中注入
buildContext(issue, acpOptions, work) {
  return {
    // ...
    emit: (event, data) => {
      if (!eventBus) return;
      try { eventBus.emit(event, data); } catch (e) { log.warn(...); }
    },
    log: (eventType, data) => {
      if (!workflowLogRepo) return;
      try { workflowLogRepo.insert(issue.id, null, eventType, data); } catch (e) { log.warn(...); }
    },
  };
}
```

**使用**：

```typescript
// 之前
this.emitSafe(eventBus, 'build_stage_started', { issueId, ... });
this.writeLog(workflowLogRepo, issueId, 'build_started', { ... });

// 之后
ctx.emit('stage_started', { stage: 'build', ... });
ctx.log('stage_started', { stage: 'build', ... });
```

**改动量**：极小。删除各 runner 的私有方法，改为 `ctx.emit()`/`ctx.log()`。

**建议**：**顺手做**，Phase 1 或 2 都可以。

---

## 五、状态系统合并（高风险）

### 当前三套系统

```
┌─────────────────────────────────────────────────────────────────────────┐
│                         三套状态系统                                    │
├─────────────────────────┬─────────────────────────┬─────────────────────┤
│  CheckpointManager      │  StageExecutionRepo     │  StageStateService  │
├─────────────────────────┼─────────────────────────┼─────────────────────┤
│  表: pipeline_checkpoint│  表: stage_executions   │  表: stage_states   │
│                         │                         │  表: stage_tasks    │
│                         │                         │  表: stage_checks   │
├─────────────────────────┼─────────────────────────┼─────────────────────┤
│  用途: legacy 断点续传  │  用途: 执行历史归档     │  用途: Web UI 实时  │
│                         │                         │  状态投影           │
├─────────────────────────┼─────────────────────────┼─────────────────────┤
│  数据: completedSteps[] │  数据: taskResults[]    │  数据: tasks[]      │
│           [taskId...]   │         checkResults[]  │         checks[]    │
│                         │         status          │         status      │
├─────────────────────────┼─────────────────────────┼─────────────────────┤
│  消费者: runner resume  │  消费者: 审计/查询      │  消费者: API / UI   │
├─────────────────────────┼─────────────────────────┼─────────────────────┤
│  生命周期: issue 存续期  │  生命周期: 持久化       │  生命周期: issue    │
│                         │                         │  存续期             │
└─────────────────────────┴───────────────────────-───┴─────────────────────┘
```

### 数据重叠分析

**CheckpointManager vs StageStateService**：

| CheckpointManager | StageStateService | 关系 |
|-------------------|-------------------|------|
| `completedSteps: ['T-001', 'T-002']` | `tasks: [{id:'T-001',status:'completed'}, {id:'T-002',status:'completed'}]` | **完全等价** |
| `markStepComplete(issue, stage, step)` | `upsertTask(issue, stage, {taskId, status:'completed'})` | 操作等价 |
| `getResumeSteps(issue, stage)` | `tasks.filter(t => t.status === 'completed').map(t => t.id)` | 查询等价 |

**关键发现**：CheckpointManager 存储的 `completedSteps` 和 StageStateService 的 `tasks[].status === 'completed'` **是同一回事**。

**StageExecutionRepo vs StageStateService**：

| StageExecutionRepo | StageStateService | 关系 |
|--------------------|-------------------|------|
| `taskResults: [{taskId, status, attempts, duration}]` | `tasks: [{taskId, status, attempts, duration}]` | **字段等价** |
| `checkResults: [{name, status}]` | `checks: [{checkName, status}]` | **字段等价** |
| 一次 stage 执行一条记录 | task/check 多条记录 | 结构不同 |

### 差异点

| 系统 | 独特能力 | 不可替代？ |
|------|----------|----------|
| **CheckpointManager** | legacy 模式下 runner 内部 resume | aggregate 模式下用 WorkflowRun，legacy 仍需 |
| **StageExecutionRepo** | 一次 stage 执行的完整快照（JSON 数组） | 可以转为 StageStateService 的查询 |
| **StageStateService** | Web UI 实时查询、任务/检查的状态追踪 | 核心，不可删除 |

### 合并方案（评估）

**方案 A：CheckpointManager 合并到 StageStateService**

```typescript
// 删除 CheckpointManager，用 StageStateService 替代
class StageStateService {
  // 已有：upsertTask, getTasks, ...
  
  // 新增：兼容 CheckpointManager 接口
  getResumeSteps(issueId: string, stage: Stage): string[] {
    const tasks = this.getTasks(issueId, stage);
    return tasks.filter(t => t.status === 'completed').map(t => t.taskId);
  }
  
  markStepComplete(issueId: string, stage: Stage, taskId: string): void {
    this.upsertTask(issueId, stage, { taskId, status: 'completed' });
  }
}
```

**问题**：
- CheckpointManager 用 `issueNumber` 做 key，StageStateService 用 `issueId`
- CheckpointManager 的 `nextStep` 字段 StageStateService 没有
- legacy 模式的 runner 直接调用 CheckpointManager，aggregate 模式不用

**方案 B：StageExecutionRepo 合并到 StageStateService**

```typescript
// 删除 StageExecutionRepo，用 StageStateService 的 tasks/checks 替代
// 需要时从 StageStateService 查询并组装为 StageExecution 格式
```

**问题**：
- StageExecutionRepo 的 `taskResults` 是 JSON 数组（完整快照），StageStateService 是关系型
- 历史查询性能：StageExecutionRepo 一次查询，StageStateService 需要 JOIN
- 数据格式兼容性

### 风险分析

| 风险 | 影响 | 概率 |
|------|------|------|
| legacy 模式依赖 CheckpointManager 的特定行为 | 高 | 中 |
| 数据库迁移（合并表） | 高 | - |
| StageExecutionRepo 的历史数据查询性能 | 中 | 中 |
| aggregate 和 legacy 双模式兼容 | 高 | 高 |

### 结论

**不建议在 Stage Runner 统一阶段合并状态系统**。

原因：
1. **双模式（legacy + aggregate）仍在共存**，改动状态系统会同时影响两个模式
2. **CheckpointManager 和 StageStateService 的 key 不同**（issueNumber vs issueId），合并需要数据层改动
3. **收益有限**：三套系统各自工作稳定，合并主要是"代码整洁"而非"功能改进"
4. **风险极高**：状态系统是 workflow 的核心，出 bug 会导致断点续传失败、UI 状态错乱

**建议**：Stage Runner 统一完成后，**单独评估**状态系统合并。可能需要：
- 先统一 key（全部用 issueId）
- 再评估 legacy 模式是否可以退役
- 最后再考虑合并

---

## 总结：5 个方向的优先级和实施建议

| # | 方向 | 价值 | 风险 | 建议阶段 | 代码量变化 |
|---|------|------|------|----------|-----------|
| 1 | **Fix Task 统一** | 高 | 低 | **Phase 1** | -250 行 |
| 2 | **Check 配置化** | 中 | 低 | **Phase 2** | 持平 |
| 3 | **emitSafe/writeLog 上移** | 低 | 极低 | **顺手做** | -20 行 x 4 |
| 4 | **事件通用化** | 中 | 中 | **Phase 3** | -10 事件定义 |
| 5 | **状态系统合并** | 中 | **极高** | **延后单独评估** | 大量 |

**实施顺序**：
```
Phase 1: Fix Task 统一 + emitSafe/writeLog 上移（顺手）
Phase 2: Stage Runner 统一 + Check 配置化
Phase 3: 事件通用化
Phase 4: 状态系统合并（单独评估）
```
