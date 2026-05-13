# Stage Runner 统一化 — Issue 规划

## Phase 1: 基础设施（低风险，可独立发布）

### Issue #1: 统一 Fix Task 体系
- **范围**: 将 `health-fix-task.ts` / `review-fix-task.ts` / `plan-repair-task.ts` 三个独立实现合并为统一的 FixTaskHandler + PromptBuilder 注册表
- **改动**: 
  - 新增 `FixTaskHandler` (~50行) + 3 个 `PromptBuilder` (~30行 x3)
  - 删除 3 个 fix task 文件（共 648 行）
  - 删除各 runner 的 `executeReportedTask` fix task 分支
- **净效果**: -400 行代码
- **优先级**: P0
- **依赖**: 无

### Issue #2: StageContext 增加安全 emit/log 方法
- **范围**: 将各 runner 复制的 `emitSafe` / `writeLog` 私有方法上移到 `StageContext`
- **改动**: 
  - `StageContext` 新增 `emit(event, data)` 和 `log(eventType, data)` 方法
  - 删除 4 个 runner 中的重复实现（~80 行）
- **优先级**: P1
- **依赖**: 无（可和 #1 并行）

---

## Phase 2: 核心架构（Stage Runner 统一化）

### Issue #3: 提取 Task Handler
- **范围**: 从现有 runner 中提取 3 种 Task Handler
  - `AgentSessionTaskHandler`（Plan/Check，含 session pool + 引用计数）
  - `RalphTaskHandler`（Build，从 RalphExecutor 拆分，单 task 执行）
  - `ServiceCallTaskHandler`（Integrate）
- **改动**:
  - 新增 3 个 handler 文件
  - RalphExecutor 拆分为 `RalphTaskLoader` (~50行) + `RalphTaskHandler` (~150行)
  - 删除 `RalphExecutor` 的 while 循环和 `findNextPendingTask`
- **优先级**: P0
- **依赖**: #1（Fix Task 统一后，handler 体系更干净）

### Issue #4: 引入 Task Loader
- **范围**: 实现 `TaskLoader` 接口和两种 loader
  - `StaticTaskLoader`（Plan/Check/Integrate：直接返回配置，组装 prompt）
  - `RalphTaskLoader`（Build：从 tasks.json 读取 + validateDependencies + sortTasksByOrder）
- **改动**:
  - 新增 2 个 loader
  - Plan 的 prompt 组装从 runner 移到 loader
  - `DEFAULT_STAGE_DEFINITIONS` 中的 Build `tasks: []` 占位符保持，由 loader 填充
- **优先级**: P0
- **依赖**: #3

### Issue #5: BaseStageRunner 改造为配置驱动
- **范围**: 
  - `run()` 方法支持配置驱动的 task dispatch
  - aggregate 模式的单 task 执行保留（`requestedWork.kind === 'task'`）
  - `executeTasks()` 从 abstract 改为基于 `StageDefinition` 的通用实现
- **改动**:
  - `BaseStageRunner` 重构（核心骨架不变，填充逻辑改变）
  - 子类只需注册 loader/handler，不再需要实现 `executeTasks()`
- **优先级**: P0
- **依赖**: #3, #4

### Issue #6: Check 配置化
- **范围**: 将各 runner 硬编码的 Check 实例改为配置驱动
- **改动**:
  - 6 个 Check 类改为 CheckHandler（结构不变，注册方式改变）
  - `StageDefinition` 增加 `checks` 配置
  - 删除各 runner 的 `getChecks()` 方法
- **优先级**: P1
- **依赖**: #5（runner 支持配置驱动后才能接入）

---

## Phase 3: 统一与清理

### Issue #7: 统一为单 StageRunner
- **范围**: 
  - 移除 `PlanStageRunner` / `BuildStageRunner` / `CheckStageRunner` / `IntegrateStageRunner` 四个子类
  - 只剩一个通用 `StageRunner`
  - stage 差异完全由 `StageDefinition` 配置决定
- **改动**:
  - 删除 4 个 runner 文件（~1500 行）
  - `WorkflowEngine` 的 runner 注册改为单例
- **优先级**: P1
- **依赖**: #5, #6

### Issue #8: 事件通用化
- **范围**: 将 `ralph_task_update` / `ralph_loop_progress` / `build_stage_*` / `plan_round_*` / `integration_*` 等 stage 特有事件统一为通用事件
- **统一后事件**:
  - `stage_started` / `stage_completed` / `stage_failed`
  - `stage_task_update`
  - `stage_progress`
  - `stage_check_update` / `stage_check_complete`
- **改动**:
  - `EventBus` 删除 ~10 个 stage 特有事件定义
  - 后端 emitter 统一
  - **Web UI 4 个 hooks 需要迁移**（主要工作量）
- **优先级**: P2
- **依赖**: #7（runner 统一后才能统一事件 emit 逻辑）

---

## Phase 4: 延后评估

### Issue #9: 状态系统合并（CheckpointManager / StageExecutionRepo / StageStateService）
- **范围**: 评估三套状态系统是否可合并为一套
- **风险**: 极高（涉及 legacy + aggregate 双模式、数据库迁移、断点续传兼容性）
- **优先级**: P3
- **依赖**: #7 完成后单独评估

---

## 依赖关系图

```
#1 Fix Task 统一 ──┐
#2 emitSafe 上移 ──┤
                   ▼
#3 Task Handler 提取 ──► #4 Task Loader ──► #5 BaseStageRunner 改造
                                                    │
                                                    ▼
#6 Check 配置化 ────────────────────────────────────┤
                                                    ▼
#7 统一为单 StageRunner ──► #8 事件通用化
                                                    │
                                                    ▼
#9 状态系统合并（延后评估）
```

## 工作量估算

| Phase | Issue | 预估工作量 | 测试影响 |
|-------|-------|-----------|----------|
| 1 | #1 Fix Task | 1-2 天 | 需要验证 fix task 行为不变 |
| 1 | #2 emitSafe | 2-4 小时 | 极小 |
| 2 | #3 Handler | 2-3 天 | 需要验证所有 stage 的 task 执行 |
| 2 | #4 Loader | 1-2 天 | 需要验证 Build task 加载 |
| 2 | #5 Runner | 2-3 天 | 核心，需要全量回归 |
| 2 | #6 Check | 1-2 天 | 需要验证所有 check |
| 3 | #7 统一 | 1 天 | 删除代码，主要是清理 |
| 3 | #8 事件 | 2-3 天 | Web UI 需要回归 |
| 4 | #9 状态 | 待定 | 待定 |

**总计**: ~10-15 工作日（不含 #9 和测试时间）
