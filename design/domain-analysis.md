---
purpose: "领域分解与上下文映射的目标态：子域（问题空间）+ 限界上下文及关系（解空间），用于判断一个改动落在哪个域。"
include:
  - "子域划分与判据。"
  - "限界上下文清单、关系模式、依赖不变量。"
  - "改动归属的判断规则。"
exclude:
  - "运行时边界与放置规则（见 architecture.md）。"
  - "各域内部的模型细节。"
style:
  - "正文是目标态 spec，现状差距收末节。"
---

# 领域分析与上下文映射

本文回答"一个改动落在哪个域"：先按**问题空间**分子域，再按**解空间**列限界上下文与关系。

## 方法

- 领域是问题空间；子域是问题空间的收敛，一个子域代表**一类需要面对的问题**。
- 判据：
  - 拿掉一个候选，**哪一类问题会无处安放、泄漏到别的子域**？答不出，它就不是子域。
  - 两个候选是否**总被一起思考**？若是，应合为一个。
- 限界上下文按**统一语言的内部一致性**切分，理想上与子域 1:1，部署边界会切出额外上下文。
- 子域归属按问题类判定，**不按代码模块**；关系箭头 = 模型依赖方向（与价值链方向不一定一致）。

## 问题空间：子域

### 核心域 Workflow（工作流生产引擎）

**问题类**：如何让工作**自治、正确地**向前流动——推进、调度、分发、审批门、失败修复、恢复续跑、解释执行报告并裁定下一步。这是驱动 issue 从 Draft 走到 Done 的生产线本身，Mohist 的核心价值载体。

执行事实与状态裁判分离（四行诗见 [`architecture.md`](architecture.md)）：执行层只产生事实，Workflow 裁定状态——这是它能被信任地自治运行的前提。

### 支撑子域

上下文与子域 1:1，统一语言一并列出：

| 子域 | 问题类 | 统一语言 | 归属判别 |
|------|--------|----------|----------|
| **Issue** | 工作是什么、如何组织、进展如何 | issue / epic / status / prerequisite / priority / risk / draft / done | Epic 是同一问题类的组织粒度（单元层/组织层），归 Issue，不是独立子域 |
| **Project Space** | 在什么环境、如何隔离、用什么配置执行 | project / repository / isolation / variable / default branch / prompt | 横切环境容器，对 Issue/Workflow/Agent 都提供环境+隔离+配置，不专门服务 Issue；prompt 归此（唯一可配置层，内置 .prompt 只是 loader fallback，详见 [`prompt-management.md`](prompt-management.md)） |
| **Agent** | 用什么 AI 智能体执行 | Agent / AgentJob / AgentJobInput / WorkResult / AssignRunner | 叶子；对 Session 仅有 job 失败善后的单向弱耦合，不构成归属 |
| **Session** | 一次执行如何被记录、压缩、查询、审计 | AgentSession / Transcript / Context / Usage / RuntimeEvents / Lineage | 横向叶子；判别见下 |
| **Runner** | 执行资源在哪、是否健康、租约给谁 | resource / heartbeat / lease / registration | 执行侧协调者，可替换，依赖 Agent |
| **Skill·Explore** | 把模糊需求提炼成清晰、有边界的 issue | — | 价值链入口；探索的执行可委托外部 agent，那是执行细节 |

**Session 独立成域的判别**：**被产生 ≠ 归属**——判据是问题独立、耦合单向、消费者多元（被 Issue / Workflow / Agent / Runner / Api 五个上下文消费），而非"谁产生了它"。跨域报告（join 多域读模型的活动 feed、成本等）不归 Session，归 AgentOps——塞进叶子会让 Session 反向依赖业务上下文，破坏叶子不变量。

### 读侧报告子域：AgentOps

**问题类**：跨多个业务子域组装**只读报告**——活动 feed、单次交付成本、跨聚合看板。

它**被允许依赖全部业务子域**（Session + Issue + Workflow + Runner）：它不是叶子，是位于业务域之上的读侧消费者。存在的意义是让"依赖全部域"这件事有名、合法、可被架构守护，让 Session 重回真叶子。

### 不是子域的概念 / 通用子域

- **Artifact**：由 Workflow 产生、被 Workflow 裁判消费、挂在 Issue 之上——没有独立问题类，属 Workflow 子域。
- **OpenSpec**：外部工具（像 git），不得当领域概念。
- **通用子域**：`Label` / `User` / `SystemInfo`——支撑设施，不承载业务规则。
- **横切技术层**（非业务域）：`Events` / `Api` / `Infrastructure`。

## 解空间：限界上下文与关系

**客户端**：Web、CLI（mo）。**技术适配器（非上下文）**：runner 进程（TS）——纯执行器，无领域模型；"Runner"上下文只在 server 端，runner 进程只机械执行（接 task → 调 Agent → 跑 check → 回报事实），定义权在 Workflow（task/check/profile）与 Agent（coder）。

关系模式（DDD）：Customer/Supplier、Conformist、ACL、OHS、Published Language（PL）、Shared Kernel（SK）。

| # | 上游 (U) | 下游 (D) | 模式 | 流动的内容 | 说明 |
|---|---------|---------|------|-----------|------|
| 1 | Workflow | Issue | Customer/Supplier | profile 选用、run 创建、裁定/产出回读 | Workflow 不知"issue"；Issue 消费引擎 |
| 2 | Workflow | Runner | OHS + PL（执行契约） | task 派发、事实回报 | Workflow 不依赖 Runner；Runner 填洞 |
| 3 | Project Space | Workflow | PL | 项目变量 | Workflow 只用不透明 projectId，不模型依赖 |
| 4 | Project Space | Issue | SK | ProjectId（共享身份）、仓库引用 | Issue 住在 project 里 |
| 5 | Issue | Skill·Explore | OHS + PL（issue 创建） | issue 体/模板 | Skill 遵奉创建契约产出 issue；价值链上游、模型下游 |
| 6 | Agent | runner 进程 | Conformist | agent 定义 | runner 遵奉定义调 coder |
| 7 | Runner | runner 进程 | PL | 心跳/租约 | Runner 拥有资源模型 |
| 8 | Server | Web | OHS + PL（HTTP API） | API DTO | Web 遵奉；含只读消费 Workflow artifact 与 Session 读模型 |
| 9 | Server | CLI | OHS + PL（HTTP API） | API DTO | CLI 遵奉 |
| 10 | Generic | Issue 等 | SK / PL | 标签、用户身份 | 通用，不承载业务规则 |
| 11 | Session | Issue / Workflow / Api / AgentOps | OHS + PL（session 读模型） | session DTO | 消费者只读遵奉，Session 拥有模型 |
| 12 | Runner / Agent | Session | PL | runtime events append（Runner）、关闭事件（Agent 善后） | 向 Session 写执行痕迹；Session 不反向依赖写者 |
| 13 | Session / Issue / Workflow / Runner | AgentOps | OHS（各域读模型） | 跨域只读报告组装 | AgentOps 允许依赖全部域 |

**Prompt 的两条架构约束**（prompt 属 Project Space）：

- **Workflow 只用 key 引用 prompt**（`WorkflowDefinition` 里的字符串），不依赖 prompt 解析——零耦合。
- **prompt 文本由执行方（runner）在执行那一刻按需解析**（lazy、只取一条、扛大 prompt），契合"执行事实与状态裁判分离"。

## 依赖方向与不变量（硬约束）

```
Workflow 核心                  ← 下行只见「抽象执行契约」
        │ 端口
        ▼
Runner（执行侧）               ← 依赖 Agent
        │
        ▼
Agent（智能体）                ← 叶子，谁都不依赖

Session（执行痕迹）            ← 横向叶子：被多上下文消费，不反向依赖任何业务上下文
```

- **Workflow 不依赖任何业务上下文**（Issue / Agent / Session / Runner / Project Space 的模型都不依赖）。它只对抽象端口说话、消费事实做裁定。把具体执行知识挡在外面**不是风格选择，是自治的前提**。
- **Issue → Workflow 单向**。Workflow 不知"issue"。
- **Runner 依赖 Agent**；**Agent 是叶子**，对 Session 只有单向善后耦合。
- **Session 是横向叶子**：模型（transcript/context/usage/lineage）独立演化，不反向依赖任何业务上下文。
- **runner 进程是基础设施**：遵奉 Workflow 执行契约、依赖 Agent 定义、向 Runner 注册心跳。
- **ProjectId 是共享身份**，但不构成 Workflow 的模型依赖。
- **Artifact 属 Workflow**，非独立上下文。

## 判断规则

按问题类判定一个改动落在哪：

- 定义"阶段、任务、检查、状态推进、调度、审批策略" → **Workflow**
- 定义"工作单元的属性、生命周期、依赖、组织" → **Issue**
- 仓库绑定、数据隔离、执行配置、prompt 库 → **Project Space**
- 智能体定义、执行发起、job 分派、report 校验 → **Agent**
- 执行过程的记录、transcript、context 压缩、usage 统计、session 查询 → **Session**
- 执行资源注册、心跳、租约 → **Runner**
- 跨多域组装只读报告 → **AgentOps**
- 标签、用户、系统信息 → 通用子域，不承载业务规则

## 待定决策

- **#1 Workflow↔Issue 定为 Customer/Supplier**。若后续要求 Workflow 对 Issue"零反馈"，收紧为 Conformist。
- **Skill·Explore 的双重身份**（价值链上游 / 模型下游）暂按 #5 处理。若后续认定 Skill 应在模型上定义 issue 的"需求形态"，则它转为上游。

## 现状偏差（迁移项）

正文是目标态，以下差距逐步收敛：

- **默认 `WorkflowDefinition` 内容错位**：`MohistWorkflow` + 内置 workflow yaml 是应用级配置，却在 `Issue/Services/WorkflowProfiles/`，应挪到应用配置层；profile 配置本身（template 选择 + variables）留原处是对的，详见 [`workflow/boundaries/issue.md`](workflow/boundaries/issue.md)。由此 `ProjectWorkflowProfileManager` 反向引用 Issue，搬走默认定义即消除。
- **Session 读侧反向依赖业务上下文**：`AgentSessionQuerier` / `AgentUsageReporter` / `AgentActivityFeedAssembler` 为组装跨域报告而反向 using Issue / Runner / Workflow，违反叶子不变量。解法即 AgentOps；待类迁到 `Mohist.Server.AgentOps.*` 后纳入 ArchUnit 守护（issue #372）。
- **模块目录与归属**：`Epic/` 属 Issue 子域；`Sessions/` 与 `Agent/` 是两个独立子域的两个目录，无需合并。
