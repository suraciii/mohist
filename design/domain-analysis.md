# 领域分析

本文记录 Mohist 领域分解的**目标态**——我们渴望达到的子域划分，用于判断一个改动落在哪个域、是否触碰核心域。当前代码可能尚未达成，偏差见末尾「现状偏差」。

## 方法

- **领域是问题空间**——Mohist 要解决的全部问题。
- **子域是对问题空间的收敛**，得到一个有限的问题空间。一个子域代表**一类我们需要解决、需要面对的问题**。
- 判据：
  - 拿掉一个候选，**哪一类问题会无处安放、泄漏到别的子域**？答不出，它就不是子域。
  - 两个候选是否**总被一起思考**？若是，说明它们还没真正收敛成两个，应合为一个。
- 子域归属按问题类判定，**不按代码模块**。代码模块是现状的映射，可能落后于目标态（见末尾「现状偏差」）。

## 核心域：Workflow（工作流生产引擎）

**收敛的问题类**：如何让工作**自治、正确地**向前流动——推进、调度、分发、审批门、失败修复、恢复续跑、解释执行报告并裁定下一步。

这是 Mohist 的核心价值载体：驱动一个 issue 从 Draft 走到 Done 的生产线本身。

核心原则（见 [`architecture.md`](architecture.md)）：

```
Task executes.
Check verifies.
Runner reports.
WorkflowRun decides.
```

执行事实与状态裁判分离：执行层只产生事实，Workflow 裁定状态。这是 Workflow 能被信任地自治运行的前提（依赖方向见下）。

## 支撑子域

### Issue（工作项）

**收敛的问题类**：工作**是什么、如何组织、进展如何**。

- `Issue`（单元 facet）：单个工作单元——生命周期、就绪条件、完成条件、前置依赖、审批态、优先级、风险。
- `Epic`（组织 facet）：把多个工作单元组织成一个目标/里程碑，呈现目标进展。

Epic 与 Issue 是**同一类问题的两个粒度**（单元层 / 组织层），不是两个子域。拿掉 Epic，"组织"这个问题会泄漏回 Issue；但 Epic 自身又不构成"规划引擎"那种独立重问题类，故归属 Issue 子域。

与核心域关系：Workflow 的**直接输入**。**目标为单向依赖**：Issue 依赖 Workflow（消费引擎、选用 profile），Workflow 不反向依赖 Issue——它只操作抽象 run，不知"issue"是什么（详见「依赖方向」）。核心价值在 Workflow 引擎，Issue 是被处理的输入。

### Project Space（执行环境）

**收敛的问题类**：在**什么环境、如何隔离、用什么配置**去执行。

Project 是**横切的执行环境容器**：git 仓库绑定、数据隔离、项目级变量、**项目级 prompt 库**。它对 Issue、对 Workflow、对 Agent 都提供"环境 + 隔离 + 配置"，不专门服务 Issue。prompt 归此（唯一可配置层；内置 .prompt 只是 loader fallback；无 issue 级覆盖）；详见 [`prompt-management.md`](prompt-management.md)。

与 Issue 是**两类不同的问题**：一个想"仓库与边界"，一个想"工作项状态机"，互不渗透。故独立成域，不并入 Issue。

### Agent（智能体）

**收敛的问题类**：用**什么 AI 智能体去执行 + 执行留下什么痕迹**。

- `Agent`（执行者 facet）：coder agent（如 opencode）的定义、配置，以及如何发起一次编码 job。
- `Session`（痕迹 facet）：一次智能体执行过程的记录、回放、审计。

Session 属于 Agent——是 Agent 执行后留下的痕迹，是同一类问题，不是独立子域。

**Agent 是叶子域**：被 Runner 依赖，但**不反向依赖 Workflow**。

### Runner（执行资源）

**收敛的问题类**：执行资源**在哪、是否健康、租约给谁**——资源注册、心跳、租约。

Runner 是执行侧协调者，可替换。它依赖 Agent（真正发起编码 job），并把执行事实报告给 Workflow。

### Skill·Explore（需求提炼）

**收敛的问题类**：把**模糊的需求/想法，提炼成清晰、有边界的 issue**。

它是价值链的入口——产出喂给 Issue、再由 Workflow 驱动的那个工作单元。探索的**执行**可委托外部 AI agent（OpenCode、Claude Code 等）通过 skill 完成，但那是执行细节（类比 runner 进程执行 task），作为子域它属于系统。

## 依赖方向（硬约束）

```
Workflow 核心                  ← 下行只见「抽象执行契约」
（推进 / 调度 / 裁定）             （task 执行 / check 验证 / 报告事实）
        │ 端口
        ▼
Runner（执行侧）               ← 依赖 Agent
        │
        ▼
Agent（智能体 + session 记录）     ← 叶子，谁都不依赖
```

- **Workflow 核心不依赖 Runner，也不依赖 Agent。** 它只对着一个抽象端口说话（"去执行这个 task / 把事实报告回来"），由 Runner 来填这个洞。
- **Runner 依赖 Agent**，因为它是真正去调智能体执行的一侧。
- **Agent 是叶子**，不反向依赖 Workflow。

把 Agent/Runner 的具体知识挡在 Workflow 之外，**不是风格选择，是 Workflow 能自治的前提**：它不知道"谁来执行、怎么执行"，只消费事实做裁定，因此可被信任地自治运行。

## 不是子域的概念

- **Artifact（产出物）**：穿行于 Workflow/Issue 之间的概念——由 Workflow 各阶段**产生**，被 Workflow 裁判**消费**，挂在 Issue **之上**。它没有自己独立要解的一类问题，是 Workflow 子域内的概念，不是子域。
- **OpenSpec**：外部工具（像 git），不是领域名词。文档中不得把外部工具当作领域概念出现。

## 通用子域

支撑性设施，非业务核心，不差异化：

- `Label` —— 标签定义
- `User` —— 用户
- `SystemInfo` —— 系统信息

以及横切的技术层（非业务域）：`Events`、`Api`、`Infrastructure`（持久化、配置、托管、Orleans、序列化、workspace）。

通用子域不应承载业务规则。

## 判断规则

按问题类判定一个改动落在哪：

- 定义"阶段、任务、检查、状态推进、调度、审批策略" → **Workflow**
- 定义"工作单元的属性、生命周期、依赖、组织" → **Issue**
- 仓库绑定、数据隔离、执行配置 → **Project Space**
- 智能体定义、执行发起、执行会话记录 → **Agent**
- 执行资源注册、心跳、租约 → **Runner**
- 标签、用户、系统信息 → 通用子域，不应承载业务规则

## 现状偏差（迁移项）

本文是目标态。当前代码与目标的偏差，逐步收敛：

- **默认 `WorkflowDefinition` 内容错位**：`MohistWorkflow` + `mohist-default.workflow.yaml` 是应用级配置，却躺在 `Issue/Services/WorkflowProfiles/`——应挪到应用配置层。workflow profile 配置本身（template 选择 + variables）是 Issue/Project 自己的配置，留原处是对的；详见 [`boundaries/workflow-issue.md`](boundaries/workflow-issue.md)。
- **Workflow↔Issue 目标为单向依赖**（Issue→Workflow）；目前 `ProjectWorkflowProfileManager` 因拿 `MohistWorkflow.Definition` 而反向引用 Issue，搬走默认定义即消除。
- **模块目录与子域归属**未对齐：`Epic/` 属 Issue 子域、`Sessions/` 属 Agent 子域，目前是独立模块目录。物理是否合并视重构成本，归属以本文为准。

## 与文档的关系

- `design/` 下的本文与 [`architecture.md`](architecture.md) 面向开发者，记录领域与边界。
- 本文是**问题空间**（子域）；[`context-map.md`](context-map.md) 是**解空间**（限界上下文及其映射），两者互补。
- `docs/` 下的用户文档按**功能组**组织（见 `docs/README.md`），不按本领域分解组织——用户关心的是"能用它做什么"，不是领域划分。
