# Workflow 执行单元设计讨论：Check vs Step

**日期**: 2026-05-02
**参与**: 用户 + opencode explore
**状态**: 进行中 — 核心设计理念已明确，Check vs Step 待决策

---

## 本次讨论确立的核心设计理念

### 1. 核心价值：不用人来守护执行

> "mohist 应该持续地不停地推进 issue，如果一个 issue 停下，那必然意味着此 issue 需要人类的介入，否则就应该持续不断地进行下去。"

mohist 是**自动驾驶系统**，不是**辅助驾驶系统**。

- **传统工作流**（人类驾驶）：人类创建 PR → 人类编写代码 → 人类运行测试 → 人类审查 → 人类合并
- **mohist 工作流**（自动驾驶）：用户创建 Issue → AI 设计方案 → AI 实现代码 → AI 验证质量 → 用户确认 → 自动合并

**关键规则**：每个阶段都应该有"自动推进"的默认行为，只在遇到"人类决策点"时暂停。

### 2. AI Agent 成本意识

- **不并行执行 AI agent**：AI agent 运行有成本，每次运行都要有价值
- 串行执行是刻意设计，不是技术限制
- 如果并行不会增加价值，就不应该并行

### 3. 审批与推进分离

- **Approve** = 用户行为（设置 approval 状态）
- **Stage 推进** = mohist 行为（检测条件满足后自动推进）
- Plan stage 和 Check stage 的 approval 是同一机制的不同实例

### 4. 统一基础设施，保留领域语义

- **底层统一**：所有阶段都用 CheckpointManager、EventBus、WorkflowEngine
- **上层分化**：Plan 用 Rounds（生成流程）、Build 用 Tasks（实现流程）、Check 用 Checks（验证流程）
- 每个 stage 保持自己的领域概念，但都用同一套基础设施

### 5. Stage 的本质是"活动类型"而非"环境"

- **Plan** = 设计活动（生成 artifacts）
- **Build** = 实现活动（执行 tasks）
- **Check** = 验证活动（运行 checks）

这与 CI/CD pipeline 的 Stage（Build→Test→Deploy 环境）不同。

---

## 遗留决策：Check vs Step

### 背景

当前三个阶段的执行单元各不相同：

| Stage | 当前执行单元 | 当前执行器 |
|-------|-------------|-----------|
| Plan | RoundConfig[] | AcpRoundRunner |
| Build | Task[] | RalphExecutor |
| Check | Check[] | CheckStageRunner |

**问题**：三套执行代码、三套 checkpoint 格式、Check 不落库。

### 讨论过的方案

**方案 A：统一为 Check**
- 所有阶段的执行单元都叫 Check
- Plan 的检查项：proposal-check、specs-check、design-check...
- Build 的检查项：task-graph-check
- Check 的检查项：build-test-check、ai-review-check、user-approval-check

**被否决的原因**：
- Check 的语义是"验证已有事物"，但 Plan/Build 是"创造新事物"
- 用户看到"Check failed"时，Plan 的"生成失败"与 Check 的"验证失败"行为完全不同
- 语义扭曲导致产品心智混乱

**方案 B：统一为 Step**
- 所有阶段的执行单元都叫 Step
- Plan 的 steps：proposal-step、specs-step...
- Build 的 step：task-graph-step
- Check 的 steps：build-test-step、ai-review-step...

**被质疑的原因**：
- Step 是中性词，丢失了 Check 的精准语义
- Plan 的 rounds 有严格顺序和依赖，Build 的 tasks 有 DAG，Check 的 checks 是独立的——这些差异被掩盖

**方案 C：保留三个领域概念，统一底层**
- Plan：Rounds（生成流程）
- Build：Tasks（实现流程）
- Check：Checks（验证流程）
- 统一 CheckpointManager、EventBus、持久化

**当前倾向**：方案 C，但需要解决 Check 不落库的问题。

### 关键分歧

**用户提出的核心问题**：
> "你要思考 mohist 如何推进 issue，一个 issue 什么情况下可以进行到下一阶段？是本阶段所有任务都完成后就能进入下一阶段，还是本阶段所有检查项都通过后才能进入下一阶段？"

这揭示了更深层的分歧：

- **"任务完成"语义**（Plan/Build）：AI 做了该做的事，产出已生成
- **"检查通过"语义**（Check）：产出符合标准，可以交付

**当前答案**：
- Plan：任务完成（rounds 跑完）+ 用户审批 → Build
- Build：任务完成（tasks 跑完）→ Check（无审批）
- Check：检查通过（checks pass）+ 用户审批 → Done

**不一致**：Plan/Build 用"任务完成"，Check 用"检查通过"。

### 待决策

1. 是否需要统一三个阶段的推进标准？
2. 如果统一，统一为"任务完成"还是"检查通过"？
3. Check 的持久化如何接入统一基础设施？

---

## 相关文件

- `design/plan.md` — Plan stage 设计（含已弃用的 Job 概念）
- `design/build.md` — Build stage 设计（含已弃用的 Job 概念）
- `design/check.md` — Check stage 设计（含已弃用的 Job 概念）
- `talks/2026-04-01-stage-model.md` — Stage 模型初始设计（含 Job 概念）
- `packages/cli/src/workflow/` — 实际代码实现
