# 上下文映射（Context Map）

本文描绘 Mohist 上下文映射的**目标态**——我们渴望达到的限界上下文划分与关系（to-be）。当前代码可能尚未达成，偏差见末尾「现状偏差」。与 [`domain-analysis.md`](domain-analysis.md) 互补：

- [`domain-analysis.md`](domain-analysis.md) = **问题空间**：子域（一类问题）。
- 本文 = **解空间**：限界上下文（模型 + 统一语言保持内部一致的范围）及其依赖与协作。

## 方法

- 限界上下文按**统一语言的内部一致性**切分，理想上与子域 1:1，但**部署边界会切出额外上下文**。
- 关系模式（DDD）：Customer/Supplier、Conformist、Anticorruption Layer、Open Host Service（OHS）、Published Language（PL）、Shared Kernel（SK）、Partnership。
- 箭头方向 = **模型依赖方向**（U 上游/被依赖 → D 下游/依赖者）。注意价值链方向与模型依赖方向不一定一致。

## 限界上下文清单

**控制平面（server）**

| 上下文 | 拥有的统一语言 |
|---|---|
| **Workflow**（核心，最受尊重） | stage / task / check / workflow run / profile / decision / gate / **artifact**（产出归此） |
| **Issue** | issue / epic / status / prerequisite / priority / risk / draft / done |
| **Project Space** | project / repository / isolation / variable / default branch / **prompt（project-scoped 资源，issue 为子 scope）** |
| **Agent** | agent / coder / job —— 叶子（执行者 facet） |
| **Session** | agent session / transcript / context / usage / runtime events / lineage —— 横向叶子（执行痕迹，被多上下文消费） |
| **Runner** | resource / heartbeat / lease / registration（纯 server 端） |
| **Skill·Explore** | 需求探索 → 产出 issue（价值链入口） |
| **AgentOps** | 跨域只读报告组装（活动 feed / 单次交付成本 / 跨聚合看板）——允许依赖全部业务上下文 |
| Generic | label / user / system info（不承载业务规则） |

**客户端**：Web、CLI（mo）

**技术适配器（非上下文）**：runner 进程（TS）——纯执行器（Agent Runner），无领域模型。

> 关键：**"Runner" 上下文只在 server 端**。runner 进程不是上下文，它只机械执行（接 Workflow 的 task → 调 Agent → 跑 check → 回报事实），定义权在 Workflow（task/check/profile）与 Agent（coder）。

## 上下文关系映射

| # | 上游 (U) | 下游 (D) | 模式 | 流动的内容 | 说明 |
|---|---|---|---|---|---|
| 1 | Workflow | Issue | Customer/Supplier | profile 选用、run 创建、裁定/产出回读 | Workflow 不知"issue"；Issue 消费引擎 |
| 2 | Workflow | Runner | OHS + PL（执行契约） | task 派发、事实回报 | Workflow 不依赖 Runner；Runner 填洞 |
| 3 | Project Space | Workflow | Published Language | 项目变量 | Workflow 只用不透明 projectId，不模型依赖 |
| 4 | Project Space | Issue | Shared Kernel | ProjectId（共享身份）、仓库引用 | Issue 住在 project 里 |
| 5 | Issue | Skill·Explore | OHS + PL（issue 创建） | issue 体/模板 | Skill 遵奉创建契约产出 issue；**价值链上游、模型下游** |
| 6 | Agent | runner 进程 | Conformist | agent 定义 | runner 遵奉定义调 coder |
| 7 | Runner | runner 进程 | Published Language | 心跳/租约 | Runner 拥有资源模型 |
| 8 | Server | Web | OHS + PL（HTTP API） | API DTO | Web 遵奉 |
| 9 | Server | CLI | OHS + PL（HTTP API） | API DTO | CLI 遵奉 |
| 10 | Generic | Issue 等 | Shared Kernel / PL | 标签、用户身份 | 通用，不承载业务规则 |
| 11 | Session | Issue / Workflow / Api / AgentOps | OHS + PL（session 读模型） | session DTO（coder-sessions、health、列表） | Session 是横向可观测域；消费者只读遵奉，Session 拥有模型 |
| 12 | Runner / Agent → Session | Session | Published Language | runtime events append（runner）、session 关闭事件（Agent job 失败善后） | 向 Session 写执行痕迹；Session 不反向依赖写者 |
| 13 | Session / Issue / Workflow / Runner | AgentOps | OHS（各域读模型） | 跨域只读报告组装 | AgentOps 位于业务域之上，允许依赖全部域；不属于任何叶子 |

补充：Web 同时**只读消费** Workflow 的 artifact 与 Session 的读模型（各自 OHS 暴露，Web 遵奉），是 #8 的细化，非新上下文。**跨域报告组装**（活动 feed、成本等 join 多域读模型）归 AgentOps，不归 Session——把它塞进 Session 会破坏叶子不变量。

**AgentOps 尚无代码落点**：当前 `AgentActivityFeedAssembler` / `AgentUsageReporter` 仍在 `Sessions/Services/` 命名空间下（ArchUnit 把它们当 Sessions 域的类）。AgentOps 作为独立域纳入 ArchUnit 守护，待 issue #372 把这些类迁到 `Mohist.Server.AgentOps.*` 后生效。

**Prompts 属于 Project Space 上下文**（project-scoped，唯一可配置层；内置 .prompt 只是 loader fallback；详见 [`prompt-management.md`](prompt-management.md)）。两点架构约束：
- **Workflow 只用 key 引用 prompt**（`WorkflowDefinition` 里的字符串），不依赖 prompt 解析——零耦合。
- **prompt 文本由执行方（runner）在执行那一刻按需解析**（lazy、只取一条、扛大 prompt），契合"执行事实与状态裁判分离"。

## Context Map

```
                         ┌──────────────┐
                         │ Skill·Explore│ ◄价值链入口（产出 issue）
                         └──────┬───────┘
                  (#5 CF, issue 创建 PL) 价值链上游 / 模型下游
                                 ▼
  ┌──────────────────── SERVER（控制平面）─────────────────────────┐
  │                                                                 │
  │   ┌──────────────┐ (#3 PL:变量)  ┌──────────────────┐ (#1 C/S)  │
  │   │ Project Space│──────────────►│     Workflow      │◄─────────┐│
  │   │  (env/iso)   │  (#4 SK:pid)  │  core / 含artifact │          ││
  │   └──────▲───────┘────────┐      └────────┬──────────┘          ││
  │          │(#4 SK:pid)      │               │ (#2 OHS+PL 执行契约)││
  │          │                 │               ▼                     ││
  │          │          ┌──────┴───────┐  ┌──────────┐               ││
  │          └──────────│    Issue     │  │  Runner  │◄── (#7 PL) ───┐││
  │                     │ (Issue+Epic) │  └────┬─────┘               ││
  │                     └──────────────┘       │                     ││
  │   (#10 SK/PL)                              │                     ││
  │   ┌──────────┐   ┌──────────┐              │                     ││
  │   │ Generic  │   │  Agent   │◄──(#6 defs)──┘ (leaf, 执行者)        ││
  │   │Label/User│   │ (defs)   │              │                     ││
  │   └──────────┘   └────┬─────┘              │                     ││
  │                       │                    │                     ││
  │   ┌───────────────────┴────────────────────┘                     ││
  │   │  #11/#12  横向：Issue/Workflow/Api 读消费，Runner/Agent 写      ││
  │   ▼                                                               ││
  │ ┌──────────────┐                                                   ││
  │ │   Session    │ (横向叶子, 执行痕迹: transcript/context/usage)     ││
  │ └──────────────┘                                                   ││
   └─────────────────────────────┬──────────────┼─────────────────────┘
     (#8/#9 OHS+PL HTTP API)    │              │
            ┌───────────────────┴───┐    ┌─────▼──────────────┐
            ▼                       ▼    │ runner 进程 (TS)   │ 非上下文
        ┌──────┐               ┌──────┐  │ 纯技术执行器        │ (Agent Runner)
        │ Web  │               │ CLI  │  │ #6 Agent / #7 心跳  │
        └──────┘               └──────┘  │ 执行时按 key 取     │
                                        │ project 的 prompt   │
                                        └────────────────────┘
```

## 不变量（硬约束）

- **Workflow 不依赖任何业务上下文**（不依赖 Issue / Agent / Session / Runner / Project Space 的模型）。它只接收抽象输入、产出裁定——这是它能被信任地自治运行的前提。
- **Issue → Workflow**：单向。Issue 消费引擎、选 profile、引用 artifact。Workflow 不知"issue"。
- **Session 是横向叶子**：被 Agent（善后写入）、Runner（runtime events append）、Issue/Workflow/Api（只读消费）多向使用，但 Session 自身不反向依赖任何业务上下文。Session 的模型（transcript/context/usage/lineage）独立演化。
- **runner 进程是基础设施**，不是上下文；它遵奉 Workflow 的执行契约、依赖 Agent 定义、向 Runner 注册心跳。
- **ProjectId 是共享身份**（人人引用），但不构成 Workflow 的模型依赖。
- **Artifact 属 Workflow**，非独立上下文。

## 待定决策（关于目标本身）

- **#1 Workflow↔Issue 定为 Customer/Supplier**。若后续要求 Workflow 对 Issue"零反馈"，应收紧为 Conformist。
- **Skill·Explore 的双重身份**（价值链上游 / 模型下游）暂按 #5 处理。若后续认定 Skill 应在模型上也定义 issue 的"需求形态"，则它转为上游。

## 现状偏差（迁移项）

本文是目标态。当前代码与目标的偏差，逐步收敛：

- **默认 `WorkflowDefinition` 内容错位**：`MohistWorkflow` + `mohist-local.workflow.yaml` 是应用级配置，却躺在 `Issue/Services/WorkflowProfiles/`——应挪到应用配置层。（注：workflow profile 配置本身 = template 选择 + variables，是 Issue/Project 自己的配置，留原处是对的；详见 [`workflow/boundaries/issue.md`](workflow/boundaries/issue.md)。）
- **模块目录与上下文归属**未对齐：`Epic/`→Issue、`Sessions/`→Session（独立上下文，本文已修正，原误归 Agent）。`Sessions/` 与 `Agent/` 是两个独立子域的两个目录，无需合并。
- **Workflow↔Issue 目标为单向依赖**（Issue→Workflow）；目前 `ProjectWorkflowProfileManager` 因拿 `MohistWorkflow.Definition` 而反向引用 Issue，搬走默认定义即消除。
- **Session 读侧反向依赖业务上下文**：Session 的读侧（`AgentSessionQuerier`、`AgentUsageReporter`、`AgentActivityFeedAssembler`）已从 `Workflow/Services/Sessions/` 迁回 `Sessions/Services/`（原寄生问题已解决），但迁回后这些类为组装活动 feed / 用量报告而反向 `using` Issue / Runner / Workflow——横向叶子域反向依赖业务上下文，违反不变量。根因是把跨域报告需求塞进了叶子域；解法是抽离一个被允许依赖全部域的读侧/报告上下文（如 AgentOps），让 Session 重回真叶子。
