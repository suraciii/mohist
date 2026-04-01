# Stage 模型设计讨论

**日期**: 2026-04-01
**参与**: 用户 + opencode explore
**状态**: 已决策

---

## 起点

M1 完成后，项目中 PRD 定义 5 阶段、代码实现 4 阶段、backlog PBI 提议 3 阶段，三者未对齐。同时残留大量旧架构文档。在进入 M2 之前需要统一认知。

## 推导过程

### 1. 从软件开发本质出发

软件开发的本质是**用信息消除不确定性的过程**。有四种不确定性：

```
不确定性 1: 问题是什么（需求模糊）
不确定性 2: 怎么解决（方案未定）
不确定性 3: 做对了吗（实现质量）
不确定性 4: 解决对了问题吗（方案-实现偏差）
```

这四种不确定性有严格的依赖顺序，返工成本递增：重新聊 → 重新设计 → 重新写代码 → 全部推翻。

### 2. 反馈周期

每种不确定性的消除都是一个反馈周期。DevOps 的核心贡献是让每个层级的反馈周期尽可能短：

```
Level 0: Agent 内循环 (秒~分钟)
  write code → compile → test → fix → rerun
  Agent 自主，不需要 human

Level 1: Stage 反馈周期 (分钟~小时)
  Stage 完成 → 产出物 → human gate → 下一个 stage 或重做

Level 2: Issue 级别 (小时~天)
  多次 PLAN-BUILD-CHECK 循环后合并 → 观察效果
```

### 3. 用户的关键输入

> "为什么忽略审查阶段？我希望的是 计划 - 实现 - 检查（测试、代码审查等）循环，检查出问题后可以指定新的修复计划"

这改变了模型——不是线性管道，而是**循环**：

```
       ┌──────┐       ┌───────┐       ┌───────┐
       │ PLAN │──────▶│ BUILD │──────▶│ CHECK │
       └──┬───┘       └───────┘       └───┬───┘
          ▲                               │
          └───────── 有问题 ──────────────┘
                                          │ 没问题
                                          ▼
                                        DONE
```

### 4. Explore 不是独立 Stage → Explore 是 Pipeline 外的交互模式

**初始决策**: Explore 是 PLAN 首次迭代的固有步骤。"理解问题"是每次规划的起点，不是前置步骤。

**后续修正**: 用户提出关键洞察——需求梳理可以不在 workflow 里，而是由用户单独和 mohist 进行交互来做。这改变了模型：

```
两种交互模式:

  Explore Mode (Pipeline 外)
  ├── 用户 + mohist 自由对话
  ├── 梳理需求、澄清模糊点、做取舍
  ├── 产出: 清晰的 Issue / Change Proposal
  └── 不受 pipeline stage 约束

  Pipeline Mode (Pipeline 内)
  ├── mo issue start → 进入 pipeline
  ├── PLAN(基于明确需求做技术方案) → BUILD → CHECK
  └── 循环直到 done
```

对比 Explore 作为 Stage vs Explore 在 Pipeline 外：
- Explore 作为 Stage: 需求梳理被 pipeline 约束，节奏不自然，Agent 主导
- Explore 在 Pipeline 外: 用户主导对话，自然节奏，产出明确后才进入 pipeline

Pipeline 内遇到需求问题的两级机制：
- **小问题**（信息缺失、歧义）: Agent 用 ask_user 问具体问题，继续推进
- **大问题**（需求矛盾、方向错误）: Agent 标记 blocked，退出 Pipeline，用户回到 Explore Mode

实际证据：当前 explore session 本身就是这个机制——在 `doc-cleanup-and-stage-model` 进入 pipeline 之前梳理需求。

### 5. 审查的多角色设计

> "审查阶段可能会有多个 agent 一起来审查，比如有架构师 agent 来审查设计方案，比如有 QA 来审查测试用例，比如有 agent 来审查代码质量、可读性等"

审查不是一个人的活，是一组专业角色的并行工作。且架构师在 PLAN 阶段也参与审查方案。

### 6. 参考 DevOps Pipeline

直接映射 DevOps pipeline 模型：

```
Stage: 串行，有严格顺序
Job:   并行，全部通过 stage 才算通过
Gate:  stage 之间的 manual approval

Stage: PLAN
  ├── Job: design (plan-agent)
  └── Job: arch-review (architect-agent, needs: design)
  Gate: human

Stage: BUILD
  └── Job: implement (code-agent)
  Gate: none (自动进入 CHECK)

Stage: CHECK
  ├── Job: arch-review (architect-agent)
  ├── Job: qa-review (qa-agent)
  └── Job: code-review (reviewer-agent)
  Gate: human
```

## 最终决策

```
Issue state: draft | plan | build | check | done

Pipeline: PLAN → BUILD → CHECK (循环)
  - CHECK 失败 → 回到 PLAN
  - CHECK 通过 → DONE

Stage 内部: Stage { jobs: Job[], gate_after: none | human }
Job:        Job { agent, needs?: Job[] }

Draft 不是 stage，是 issue 创建状态
Explore 是 Pipeline 外的交互模式，不是 stage
Gate 是 stage 属性，不是独立 stage
```

## 演进路径

```
M1 (现在):   design(explore+plan) → build → done
M2:          plan → build → check → done (3 stages, 2 gates, ask_user)
M3:          + 并行 Jobs + workflow.yaml 可配置
M4:          完全自定义 pipeline
```

## 关联

- 变更提案: `openspec/changes/doc-cleanup-and-stage-model/`
- Pipeline spec: `openspec/changes/doc-cleanup-and-stage-model/specs/pipeline-model/spec.md`
- Backlog PBI (Stage 架构重设计): `prd/backlog/backlog.md` 第 6 节
