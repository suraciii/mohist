# 领域分析

本文记录 Mohist 的领域分解：核心域、支撑子域、通用子域，以及各自的边界。用于判断一个改动落在哪个域、是否触碰核心域。

领域分解以代码的顶层模块（`packages/server/src/Mohist.Server/<Domain>/`）为准。每个业务域按 `Domain / Grains / Services` 组织聚合。

## 核心域：Workflow（工作流生产引擎）

Workflow 是 Mohist 的核心价值载体。它驱动一个 issue 从 Draft 走到 Done，是价值流的生产线本身。

职责：

- 定义工作流阶段、任务、检查（workflow profile）
- 推进状态、做调度与分发决策
- 解释执行报告、裁定下一步
- 管理审批门、失败修复、恢复与续跑

核心原则（见 [`architecture.md`](architecture.md)）：

```
Task executes.
Check verifies.
Runner reports.
WorkflowRun decides.
```

执行事实与状态裁判分离：执行层只产生事实，Workflow 裁定状态。这是 Workflow 能被信任地自治运行的前提。

## 支撑子域

服务核心域，是产品的一部分，但本身不是差异化价值。

| 子域 | 模块 | 职责 | 与核心域的关系 |
|---|---|---|---|
| Issue | `Issue/` | 工作单元：生命周期、健康度、审批态、前置依赖、标签、优先级、风险 | 核心域的输入，被 Workflow 驱动 |
| Project | `Project/` | git 仓库绑定、数据隔离 | 执行上下文 |
| Epic | `Epic/` | 把多个 issue 组织成产品里程碑 | 核心域之上的规划层 |
| Agent | `Agent/` | coder agent（opencode）定义与执行 job | Build 阶段的执行者 |
| Sessions | `Sessions/` | coder 执行会话与回放 | 执行过程的记录 |
| Runner | `Runner/` | 执行资源注册、心跳、租约 | 执行事实的生产端协调，可替换 |
| Artifact | 跨 workflow / OpenSpec | 每阶段产出的可审查记录 | 流动的证据 |

Issue 与 Workflow 的耦合：`IssueGrain` 负责启动并交接给 `WorkflowGrain`。两者紧耦合，但核心价值（让工作自治流动）在 Workflow 引擎，不在 Issue 数据模型。Issue 是被处理的输入。

## 通用子域

支撑性设施，非业务核心，不差异化：

- `Label` —— 标签定义
- `User` —— 用户
- `SystemInfo` —— 系统信息

以及横切的技术层（非业务域）：`Events`、`Api`、`Infrastructure`（持久化、配置、托管、Orleans、序列化、workspace）。

## 外部化

**Skill / Explore** —— 需求探索由外部 agent（OpenCode、Claude Code 等）通过 skill 完成，产出 issue。按架构边界不属于 Mohist runtime 核心（见 [`architecture.md`](architecture.md) 的 "Agent Skill Boundary"）。

## 判断规则

- 一个改动在定义"阶段、任务、检查、状态推进、调度、审批策略"，属于核心域 Workflow。
- 一个改动在定义"工作单元的属性、生命周期、依赖"，属于 Issue。
- 一个改动只是仓库绑定、数据隔离，属于 Project。
- 一个改动是 coder 执行、会话记录，属于 Agent / Sessions。
- 一个改动是执行资源调度、租约，属于 Runner。
- 标签、用户、系统信息属于通用子域，不应承载业务规则。

## 与文档的关系

- `design/` 下的本文与 [`architecture.md`](architecture.md) 面向开发者，记录领域与边界。
- `docs/` 下的用户文档按**功能组**组织（见 `docs/README.md`），不按本领域分解组织——用户关心的是"能用它做什么"，不是领域划分。
