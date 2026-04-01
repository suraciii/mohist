## Context

M1 完成后，项目文档和 spec 处于三种不一致状态：
1. `design/` 下 9 个文件描述旧确定性状态机架构（crawlph/openclaw 时代）
2. `prd/` 使用 crawlph 命名，定义 5 阶段 (Draft/Explore/Plan/Dev/Verify)
3. 代码实现 4 阶段 (`draft | designing | implementing | done`)
4. `openspec/specs/` 中 11 个 spec 描述已删除的旧架构

经过 explore 讨论，确定了新的 stage 模型：PLAN → BUILD → CHECK 循环，源自 DevOps pipeline 设计（Stage 串行、Job 并行、Gate 作为 Stage 属性）。

## Goals / Non-Goals

**Goals:**
- 清理所有旧架构文档和 spec，消除认知噪音
- 统一 stage 模型为 PLAN → BUILD → CHECK，对齐 PRD、代码、spec
- 更新 Stage 枚举和 DB schema
- 建立 pipeline-model spec 作为后续 M2/M3 工作的基础
- 记录设计讨论到 `talks/` 目录

**Non-Goals:**
- 实现 M2 的 Event Bus、ask_user、mo attach 等功能
- 实现 workflow.yaml 配置系统
- 实现并行 Job 机制（M3 范畴）
- 实现 CHECK stage 的多审查 agent

## Decisions

### D1: Stage 模型采用 PLAN → BUILD → CHECK 三阶段循环

**选择**: 3 stages + 循环机制，CHECK 失败回到 PLAN

**替代方案**:
- 4 stages (explore/plan/build/validate): 过度拆分，explore 是 plan 首轮的一部分
- 5 stages (PRD 原始): verify 作为独立 stage 不必要，验收人和需求人是同一人
- 2 stages (M1 现状): 缺少检查反馈周期

**理由**: 基于软件开发四种不确定性（问题/方案/实现质量/方案-实现偏差）和 DevOps 反馈周期理论，3 stages 是最小完整反馈循环。CHECK 提供独立于 BUILD 的反馈视角，发现问题后带新信息重新规划。

### D2: Stage 内部结构采用 DevOps Pipeline 模型

**选择**: Stage 包含并行 Jobs，Job 可声明依赖

```
Stage {
  jobs: Job[]
  gate_after: none | human
}

Job {
  agent: Agent
  needs?: Job[]
}
```

**替代方案**:
- 每个 stage 只有一个 agent，审查内嵌为 stage 最后一步
- 审查作为独立 stage 类型

**理由**: DevOps pipeline 是成熟模型（GitHub Actions、GitLab CI），开发者熟悉。Stage 串行保证顺序，Job 并行提高效率。M1/M2 先实现单 Job per Stage，M3 扩展为多 Job。

### D3: Explore 是 Pipeline 之外的交互模式，不是 Stage

**选择**: 需求梳理在 Pipeline 之外通过 Explore Mode 完成，不作为 Pipeline 的 Stage

**替代方案**:
- Explore 作为独立 Stage (4 stages: explore/plan/build/check): 需求梳理被 pipeline 约束，节奏不自然
- Explore 折叠进 PLAN (3 stages): 把"理解需求"和"设计方案"两种本质不同的活动混在一起

**理由**: 需求梳理（理解问题、澄清模糊点、做取舍）是人类主导的对话活动，不应被 pipeline stage 约束。Explore Mode 是用户和 mohist 的自由对话，产出清晰的 Issue/Proposal 后才进入 Pipeline。当前 opencode 的 explore 模式就是这个机制的雏形。

Pipeline 内部，PLAN stage 专注于基于明确需求做技术方案。如果执行过程中遇到问题：
- **小问题**（信息缺失、歧义）：Agent 用 ask_user 问具体问题，继续推进
- **大问题**（需求矛盾、方向错误）：Agent 标记 blocked，退出 Pipeline，用户回到 Explore Mode 重新梳理

### D4: Draft 不是 Stage

**选择**: Draft 是 issue 创建时的初始 state，不是 pipeline 中的 stage

**理由**: 创建 issue（写标题和描述）是一个动作，不是需要 agent 执行的阶段。Issue 创建后直接进入 PLAN。

### D5: Gate 是 Stage 属性，不是独立 Stage

**选择**: `gate_after: none | human` 作为 stage 的属性

**替代方案**: `waiting-*` 作为独立 stage（旧做法）

**理由**: 消除 `waiting-design-review` / `waiting-review` 等伪阶段。Stage 状态用 `active | waiting_gate` 表达运行时状态。

### D6: Issue State 枚举

**选择**: `draft | plan | build | check | done`

- `draft`: issue 刚创建，尚未进入 pipeline
- `plan`: 正在规划（基于明确需求做技术方案）
- `build`: 正在构建
- `check`: 正在检查（测试、审查）
- `done`: 完成

IssueStatus 保持不变: `active | paused | blocked`

### D7: 旧文档处理策略

**选择**:
- `design/` 旧文件：直接删除，重写 3 个新文件
- `openspec/specs/` 旧 spec：直接删除
- `openspec/changes/` 已完成提案：保留（不删除）

**替代方案**: 全部移到 archive 目录保留历史

**理由**: git 历史已保留所有内容。archive 目录增加认知负担，没人会去看。

## Risks / Trade-offs

- **[Stage 枚举变更导致构建失败]** → 所有引用 `Stage.Designing` / `Stage.Implementing` 的代码需要同步更新。风险可控，M1 代码量有限。
- **[DB migration]** → 已有数据库中的 `designing` / `implementing` 值需要迁移为 `plan` / `build`。需要写 migration script。
- **[M2 依赖]** → M2 的 Event Bus、ask_user、mo attach 等功能依赖此变更定义的 stage 模型。此变更必须先于 M2 完成。
