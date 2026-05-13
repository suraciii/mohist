# Workflow Stage Runner 统一化 — Mohist Issue 规划

**日期**: 2026-05-13
**分析基础**: 
- `talks/2026-05-13-stage-runner-unification.md`（架构探索）
- `talks/2026-05-13-workflow-unification-opportunities.md`（5个可统一方向）
- `talks/2026-05-13-stage-runner-unification-dependencies.md`（依赖分析）

---

## 总体规划：5 个主题 Issue

将 9 个细分事项按**架构层级**和**依赖关系**合并为 5 个主题 issue。每个 issue 内部的事项高度相关，天然适合一起实施。

```
Issue #1: Fix Task 统一与基础设施清理
    │    （独立，可最先做）
    ▼
Issue #2: Task 执行层重构（Handler + Loader）
    │    （核心前置，定义执行接口）
    ▼
Issue #3: Stage Runner 配置化与统一
    │    （编排层重构，依赖 #2）
    ▼
Issue #4: 事件系统通用化
    │    （观察者层，依赖 #3）
    ▼
Issue #5: 状态系统合并评估
         （延后，独立评估）
```

---

## Issue #1: Fix Task 统一与基础设施清理

### 范围

1. **Fix Task 统一**
   - 将 `health-fix-task.ts` / `review-fix-task.ts` / `plan-repair-task.ts` 三个独立实现合并
   - 提取统一的 `FixTaskHandler` + `PromptBuilder` 注册表
   - 各 runner 的 `executeReportedTask` 中硬编码的 fix task 分支改为通过 Handler 调度

2. **emitSafe / writeLog 上移**
   - 将 4 个 runner 中复制的 `emitSafe` / `writeLog` 私有方法上移到 `StageContext`
   - runner 中改为 `ctx.emit()` / `ctx.log()` 调用

### 为什么放在一起

- **同一层级**：都是"基础设施清理"，不涉及核心架构变化
- **零依赖**：两者完全独立，可并行开发
- **低价值但高重复**：都是消除重复代码，为后续 runner 改造减少干扰
- **测试影响小**：行为不变，只是调用方式改变

### 预期改动

| 文件 | 动作 | 说明 |
|------|------|------|
| 新增 `workflow/task-handlers/fix-task-handler.ts` | 创建 | 统一 Fix Task 执行骨架 |
| 新增 `workflow/task-handlers/prompt-builders/` | 创建 | 3 个 PromptBuilder |
| 删除 `workflow/health-fix-task.ts` | 删除 | 195行，逻辑合并到 handler |
| 删除 `workflow/review-fix-task.ts` | 删除 | 182行，逻辑合并到 handler |
| 删除 `workflow/plan-repair-task.ts` | 删除 | 271行，逻辑合并到 handler |
| `workflow/stage-context.ts` | 修改 | 增加 `emit()` / `log()` 方法 |
| 4 个 runner | 修改 | 删除私有 emitSafe/writeLog，改为 ctx 调用 |

**净代码量**: -400 行

### 验收标准

- [ ] `health-fix-task.ts` / `review-fix-task.ts` / `plan-repair-task.ts` 已删除
- [ ] Fix Task 通过 `FixTaskHandler` 统一执行
- [ ] 所有 runner 使用 `ctx.emit()` / `ctx.log()`，无重复私有方法
- [ ] 现有测试通过（fix task 行为不变）

---

## Issue #2: Task 执行层重构（Handler + Loader）

### 范围

1. **Task Handler 提取**
   - `AgentSessionTaskHandler`：Plan/Check 用，含 session pool + 引用计数复用
   - `ServiceCallTaskHandler`：Integrate 用，直接调用服务方法
   - `RalphTaskHandler`：Build 用，从 `RalphExecutor` 拆分，单 task 执行（含内部重试）
   - 定义统一的 `TaskHandler` 接口

2. **Task Loader 引入**
   - `StaticTaskLoader`：Plan/Check/Integrate 用，从配置组装可执行 task（含 prompt 组装）
   - `RalphTaskLoader`：Build 用，从 `tasks.json` 读取 + validateDependencies + sortTasksByOrder
   - 定义统一的 `TaskLoader` 接口

3. **RalphExecutor 拆分**
   - 将 1086 行的 `RalphExecutor` 拆分为 `RalphTaskLoader` (~50行) + `RalphTaskHandler` (~150行)
   - 保留 `runRalphLoop` 作为兼容层（供现有测试使用）
   - 提取 `categorizeFailure`, `buildTaskContext` 等共享函数到 `ralph-task-utils.ts`

### 为什么放在一起

- **同一架构层**：Handler 和 Loader 是 Task 执行层的两个面（"怎么执行"和"从哪来"）
- **接口互相依赖**：Loader 的输出格式需要匹配 Handler 的输入格式
- **Ralph 是核心**：RalphExecutor 拆分是最大工作量，需要 Loader 和 Handler 同时定义
- **先定义接口，再实现**：TaskHandler 和 TaskLoader 接口定义好后，各实现可以并行

### 预期改动

| 文件 | 动作 | 说明 |
|------|------|------|
| 新增 `workflow/task-handlers/index.ts` | 创建 | TaskHandler 接口定义 |
| 新增 `workflow/task-handlers/agent-session-handler.ts` | 创建 | AgentSessionTaskHandler |
| 新增 `workflow/task-handlers/service-call-handler.ts` | 创建 | ServiceCallTaskHandler |
| 新增 `workflow/task-handlers/ralph-task-handler.ts` | 创建 | RalphTaskHandler |
| 新增 `workflow/task-loaders/index.ts` | 创建 | TaskLoader 接口定义 |
| 新增 `workflow/task-loaders/static-loader.ts` | 创建 | StaticTaskLoader |
| 新增 `workflow/task-loaders/ralph-loader.ts` | 创建 | RalphTaskLoader |
| 新增 `openspec/ralph-task-utils.ts` | 创建 | 共享函数（categorizeFailure 等） |
| `openspec/ralph-executor.ts` | 修改 | 拆分，保留兼容层 |
| 8 个 Ralph 测试文件 | 修改 | 逐步迁移到新的 Handler/Loder 测试 |

### 验收标准

- [ ] `TaskHandler` 和 `TaskLoader` 接口定义完成
- [ ] 3 个 Handler 实现通过测试
- [ ] 2 个 Loader 实现通过测试
- [ ] `RalphExecutor` 拆分为 Loader + Handler，保留兼容层
- [ ] 所有 Ralph 相关测试通过

---

## Issue #3: Stage Runner 配置化与统一

### 范围

1. **BaseStageRunner 改造为配置驱动**
   - 保留 legacy 模式（子类覆盖 `executeTasks()`）
   - 新增配置驱动模式（子类提供 `getStageDefinition()`）
   - `run()` 方法根据是否有 definition 选择执行路径
   - aggregate 模式的单 task/check 执行逻辑不变

2. **Check 配置化**
   - 将各 runner 硬编码的 Check 实例改为配置驱动
   - `CheckHandler` 注册表（类似 TaskHandler）
   - `StageDefinition` 增加 `checks` 配置

3. **各 stage 从继承改为配置**
   - 按顺序迁移：Integrate（最简单）→ Plan → Check → Build（最复杂）
   - 每个 stage 独立验证，出问题可快速回滚到 legacy 模式

4. **统一为单 StageRunner**
   - 所有 stage 稳定运行在新模式后，删除 4 个 runner 子类
   - `WorkflowEngine` 的 runner 注册从数组改为单例

### 为什么放在一起

- **同一架构层**：都是"编排层"的改造
- **强依赖关系**：必须先有配置驱动能力，才能迁移各 stage；必须所有 stage 迁移完，才能统一为单 runner
- **渐进式**：从"双模式共存"到"统一单 runner"是一个连续过程
- **测试集中**：`base-stage-runner.test.ts` (468行) 和 `integrate-stage-runner.test.ts` (1413行) 是核心测试

### 预期改动

| 文件 | 动作 | 说明 |
|------|------|------|
| `workflow/base-stage-runner.ts` | 修改 | 增加配置驱动分支 |
| `workflow/stage-definition.ts`（新增） | 创建 | StageDefinition 类型定义 |
| `workflow/domain/index.ts` | 修改 | DEFAULT_STAGE_DEFINITIONS 改为完整配置 |
| `workflow/plan-stage-runner.ts` | 修改 → 删除 | 先改为提供 definition，稳定后删除 |
| `workflow/build-stage-runner.ts` | 修改 → 删除 | 同上 |
| `workflow/check-stage-runner.ts` | 修改 → 删除 | 同上 |
| `workflow/integrate-stage-runner.ts` | 修改 → 删除 | 同上 |
| `workflow/workflow-engine.ts` | 修改 | runner 注册改为单例 |
| `tests/base-stage-runner.test.ts` | 修改 | TestStageRunner 改为提供 definition |
| `tests/integrate-stage-runner.test.ts` | 修改 | 测试通用 StageRunner |

### 验收标准

- [ ] BaseStageRunner 同时支持 legacy 和配置驱动
- [ ] Integrate 迁移到配置驱动并稳定运行
- [ ] Plan 迁移到配置驱动并稳定运行
- [ ] Check 迁移到配置驱动并稳定运行
- [ ] Build 迁移到配置驱动并稳定运行
- [ ] Check 配置化完成
- [ ] 4 个 runner 子类已删除
- [ ] WorkflowEngine 只注册一个 StageRunner
- [ ] 所有 workflow 集成测试通过

---

## Issue #4: 事件系统通用化

### 范围

1. **后端事件统一**
   - 将 stage 特有事件统一为通用事件：
     - `ralph_task_update` → `stage_task_update`
     - `ralph_loop_progress` → `stage_progress`
     - `build_stage_started` → `stage_started`
     - `plan_round_start` → `stage_task_update`
     - `integration_step_updated` → `stage_task_update`
     - ...等
   - 通用事件增加 `stage` 字段用于区分

2. **Web UI 迁移**
   - `useTaskProgress.ts`：监听 `stage_task_update` + `stage_progress`，按 `stage` 过滤
   - `useSessionTimeline.ts`：同上
   - `useActivityCards.ts`：同上
   - `useSSE.tsx`：事件转发逻辑更新

### 为什么独立成一个 Issue

- **与编排层解耦**：事件系统可以独立演进
- **Web UI 改动面大**：4 个 hooks + PipelineView，需要专门测试
- **可延后**：runner 统一后事件 emit 逻辑才稳定，此时统一事件最自然
- **用户可见**：这是前端用户能感知到的改动

### 预期改动

| 文件 | 动作 | 说明 |
|------|------|------|
| `services/event-bus.ts` | 修改 | 删除 stage 特有事件定义，保留通用事件 |
| `workflow/` 各 emit 点 | 修改 | 改为通用事件名称 |
| `web/src/hooks/useTaskProgress.ts` | 修改 | 监听通用事件，按 stage 过滤 |
| `web/src/hooks/useSessionTimeline.ts` | 修改 | 同上 |
| `web/src/hooks/useActivityCards.ts` | 修改 | 同上 |
| `web/src/hooks/useSSE.tsx` | 修改 | 事件转发 |
| `web/src/lib/types.ts` | 修改 | 事件类型定义更新 |

### 验收标准

- [ ] EventBus 无 stage 特有事件
- [ ] 所有 stage 使用同一套事件名称
- [ ] Web UI 按 `stage` 字段正确过滤事件
- [ ] PipelineView 正常显示各 stage 进度
- [ ] 端到端测试通过

---

## Issue #5: 状态系统合并评估

### 范围

1. **调研三套状态系统的重叠与差异**
   - `CheckpointManager`（legacy 断点续传）
   - `StageExecutionRepo`（执行历史归档）
   - `StageStateService`（Web UI 实时状态）

2. **评估合并可行性**
   - Key 统一（issueNumber vs issueId）
   - 数据格式兼容性（JSON 数组 vs 关系型）
   - Legacy + Aggregate 双模式影响

3. **输出评估报告**
   - 合并方案（或不合并的理由）
   - 数据迁移策略
   - 风险分析

### 为什么延后并独立

- **高风险**：状态系统是 workflow 核心，出 bug 会导致断点续传失败、UI 状态错乱
- **依赖 Legacy 退役**：需要等 aggregate 模式稳定运行、legacy 使用率低后再评估
- **非阻塞**：前三项 issue 不需要状态系统合并
- **独立决策**：可以单独开会讨论，不影响主流程

### 验收标准

- [ ] 完成三套状态系统的详细对比分析
- [ ] 输出合并评估报告（合并方案或不合并理由）
- [ ] 如果决定合并，输出详细迁移计划

---

## 实施顺序总览

```
Phase 1: Issue #1（1-2 天）
  Fix Task 统一 + emitSafe 上移
  └── 独立，零风险，先清理基础设施

Phase 2: Issue #2（3-5 天）
  Task Handler + Loader 提取
  └── RalphExecutor 拆分是核心工作量
  └── 定义 TaskHandler/TaskLoader 接口，供 #3 使用

Phase 3: Issue #3（5-7 天）
  Stage Runner 配置化与统一
  └── 渐进式：Integrate → Plan → Check → Build
  └── 双模式共存，每个 stage 独立验证

Phase 4: Issue #4（2-3 天）
  事件系统通用化
  └── 后端 + Web UI 一起改

Phase 5: Issue #5（独立评估）
  状态系统合并评估
  └── 等 aggregate 模式稳定后评估
```

**总工作量**: ~11-17 工作日（不含 #5 和测试时间）

---

## 依赖关系总结

| Issue | 前置依赖 | 可被独立开始？ |
|-------|----------|---------------|
| #1 Fix Task | 无 | 是 |
| #2 Handler+Loader | #1（推荐，非必须） | 接口定义后可独立 |
| #3 Runner 统一 | #2（必须） | 否 |
| #4 事件通用化 | #3（必须） | 否 |
| #5 状态合并 | #3（推荐） | 是（独立评估） |

---

## 风险最高的改动点

| Issue | 风险点 | 缓解措施 |
|-------|--------|----------|
| #2 | RalphExecutor 拆分（8个测试文件） | 保留兼容层，逐步迁移 |
| #3 | BaseStageRunner 双模式共存 | 渐进式，逐个 stage 验证 |
| #4 | Web UI 事件迁移 | 后端保持双发，前端逐步切 |
| #5 | 状态系统合并 | 延后评估，充分测试 |

---

## 最小化改动原则

每个 Issue 遵循以下原则：
1. **保留兼容层**：不删除旧代码直到新代码稳定
2. **双模式共存**：legacy 和配置驱动可以同时运行
3. **逐个验证**：每个 stage 独立迁移，出问题可回滚
4. **测试先行**：每个改动都有对应的测试覆盖
