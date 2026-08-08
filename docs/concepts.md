# 核心概念

理解这些概念，就能理解 Mohist 怎么把工作持续推到 Done。

## 一句话版本

> 你通常留在已有工作场所：可以通过 **Agent 接入**直接调用一个已配置的
> **Mohist Agent**，也可以让第三方的**外部 Agent**通过 **Skill** 操作 Mohist。
> Mohist 在 **Project** 中把产品目标记录为 **Issue**，由 **Workflow** 推进到 Done。多个
> 相关 Issue 组成 **Epic**；**Inline Agent**执行 task，**Mohist Agent**作为稳定代理人
> 接受委托或响应事件。

## Project（项目）

一个 Project 对应一个真实产品，是 Mohist 内的产品范围与执行边界，不是用户的聊天或
协作工作站点。

- 每个 Project 声明一个或多个 git **仓库**作为执行资源（每个仓库有资源名 + base branch），其中一个是 default 仓库
- Project 内的每个 issue 有一个**目标仓库**，不指定就落在 default 仓库
- 同时只能有一个 active project（CLI 用 `mo project use` 切换）
- 不同 Project 的数据完全隔离

```bash
mo project create my-app --path /path/to/repo   # --path 的仓库注册为 default 仓库
mo project use my-app
mo server status   # 查看整体 Server 状态（含 Runner / 容量）
```

**多个 Project 的场景**：你有 side project A、side project B，分别建 Project，按需切换。

**多个仓库的场景**：产品的 server 和 web 是两个代码库，同一个 Project 声明两个仓库，issue 按目标仓库分流。详见 [仓库](repositories.md)。

Project 下的执行发生在 **Workspace** 里：issue 启动时获得自己的干净 workspace，Slack channel 等交互入口也各自对应一个持久 workspace。详见 [Workspace](workspaces.md)。

## Issue（工作单元）

一个 Issue = 一份可进入生产线的工作。

- 标题 + body（描述需求）
- 优先级 p0（最高）~ p4（最低）
- 标签（自由文本）
- 目标仓库（这份工作在哪个代码库里执行，见 [仓库](repositories.md)）
- 进入 workflow 后会有 stage、health、approvalState 等状态
- 完成后留下一整套 OpenSpec 产物（proposal / design / specs / tasks / review）

一份需求大到横跨多个仓库时，可以把一个 issue 拆成若干**子 issue**：父 issue 追踪整体，子 issue 各自走 workflow。详见 [复合 Issue 与子 Issue](sub-issues.md)。

**Issue 的关键属性**：

| 属性 | 含义 |
|---|---|
| `status` | backlog / in-progress / done / cancelled |
| `workflowStage` | plan / build / check / integrate / done（workflow 内位置） |
| `health` | active / paused / blocked / cancelled / done（运行健康度） |
| `approvalState` | 当前是否停在审批点，等待 approve / reject 决策 |

详见 [Issue 管理](issues.md)。

## Workflow（工作流）

Workflow 是 Issue 从 idea 走到 merged 的生产线。Mohist 的默认 workflow 有 5 个阶段：

```
Draft → Plan → Build → Check → Integrate → Done
         ↑         |          |
         |         v          v
       Backlog   Build     Build
                (rejected) (integrate failed)
```

每个阶段：

- **Draft** — Issue 创建后的初始状态，没启动
- **Plan** — Inline Agent 理解需求、产出 proposal/design/specs/tasks
- **Build** — Inline Agent 按 tasks 写代码、跑测试
- **Check** — Inline Agent review 自己的产出
- **Integrate** — 合并分支回 base branch

某些阶段（默认是 Plan 和 Check）完成后会进入**审批点**，等待 approve / reject 决策；审批者不限定是谁，见下文 [Approval（审批）](#approval审批)。

详见 [工作流详解](the-workflow.md)。

### Workflow Profile

Workflow 不是写死的。每个 Project 可以拥有多个 **Workflow Profile**，并指定一个默认
Profile。Issue 可以继承默认值，也可以选择同一 Project 中的其他 Profile。

Profile 定义 stages、tasks、checks、recovery 和 approval，不保存 Variables 或 Prompt
正文。Project / Issue / Run Variables 按 scope 合并；Prompt 只在 Project 中配置。

当前内置 profile：
- `mohist/local` — 完整 5 阶段流程，本地合并（默认）
- `mohist/github-pr` — Integrate 阶段走 GitHub PR

详见 [Workflow Profile](workflow-profiles.md)。

## Epic（产品目标）

Epic 是为一个产品目标持续供料的单位。新建的 Epic 默认 `idle`，通过 **Start** 启动自动推进——Epic 会在 linked issue 完成后启动下一个可推进的 issue。

详见 [用 Epic 规划](epics.md) 了解完整生命周期。

## 外部 Agent、Inline Agent、Mohist Agent 与 Agent 接入

外部 Agent 在 Mohist 之外与用户交互，例如运行在 Slack、IDE 或其他支持 Agent 的工具中。
它通过 Mohist Skill 和 `mo` 查询、委托或操作执行层，但不是 Mohist 资源，也不由 Mohist
调度。

Inline Agent 是 Workflow 直接通过 `mohist/opencode` 等 Action 调用 Agent 能力的
方式，没有独立 Agent ID。Mohist Agent 则是 Project 内有稳定 ID、名称、Instructions
和配置的 Agent，可以从 Web UI、CLI、Agent 接入、事件路由或评论提及启动。

Agent 接入把一个 Mohist Agent 暴露到外部交互场所。例如 Slack Agent 接入让一个 Slack
Bot 代表指定的 Mohist Agent：Slack 负责接收消息和展示回复，Mohist Agent 仍负责理解、
执行和会话。接入不复制 Agent 配置，也不能在同一段对话中临时切换成另一个 Agent。

Slack 接入在用户侧呈现为两类 App。**Mohist App** 是工作区的管理入口，本身是一个内置
Mohist Agent：用户用自然语言与它对话，完成挂载、调整、诊断和创建 Agent。**Agent App**
是执行入口：每个接入的 Mohist Agent 各有独立的 Slack App 与 Bot 身份，直接接受工作并
回复结果。管理动作与工作任务各有明确身份，互不代发。

AgentSession 不是 Agent，也不是工作结果；它记录一段对话的消息、上下文、用量、
活动状态和当前 Runtime Session。Workflow TaskRun 负责 Workflow 工作，AgentJob 负责一次
Mohist Agent launch 的首次执行；后续输入继续同一个 AgentSession，但不改写 AgentJob。
AgentSession 中每条被接受的输入是 SessionInput，一次连续的 Runtime 处理是 AgentTurn；一个
Turn 可以按顺序处理多条 Input，因此消息、执行过程和工作结果不会被混成同一个状态。

完整关系见 [Agent 与 AgentSession](agent-sessions.md)；Slack 使用方式见
[Slack](slack.md)；OpenCode Action 配置见
[`mohist/opencode` Action](actions/opencode.md)。

## Approval（审批）

审批是对 workflow 阶段产物的 approve / reject 决策。阶段完成后可以进入审批点，等待审批者决定产物能否继续流动。审批不限定审批者必须是人，它是流水线上的一个角色占位。

一个审批点可以由多种机制给出决策：

- 自动检查：测试、lint、artifact 是否齐全。
- Mohist Agent 或脚本：读取证据后调用 approve / reject。
- owner：在需要人工判断时调用 approve / reject。

Workflow 不区分这些来源。谁来发起审批，是 Mohist Agent、CLI、Web UI 或外部自动化的职责；审批结果仍然只有 approve / reject。

审批与评论的归属来自认证身份：历史记录回答“这道门是谁放的”时，答案是调用者的身份（你、机器或 Agent），不是自称。`--display-name` 只是展示别名，供界面友好地显示一个名字，不参与归属。归属由 Mohist 认证层解析（见[认证与访问](auth.md)），不由调用方声明。

## Skill（Agent 能力）

Skill 是可复用的 Agent 能力说明。外部 Agent 可以安装 Mohist 分发的 Skill，理解 Mohist
的领域动作和操作边界；Mohist Agent 也可以在自己的配置中选择 Skills，并在任何入口使用
同一组能力。入口不能替 Agent 增删 Skills。

Mohist 分发四个 Skill：

| Skill | 作用 |
|---|---|
| `mohist` | 在外部 agent 里操作 Mohist（创建 issue、审批、查状态） |
| `mohist-explore` | 从产品视角探索需求，产出可进入 workflow 的结构化 issue |
| `mohist-create-issue` | 把已明确的需求创建为可独立交付的 Issue |
| `mohist-create-epic` | 创建产品目标并组织、驱动相关 Issue |

外部 Agent 的典型路径是按需加载这些 Skill，通过 `mo` 把意图交给 Mohist，再把执行状态
和结果带回原会话。Mohist Agent 的 Skills 则随 Agent 配置，在启动时固定到本次工作。
完整关系见 [Skill 机制](skills.md)。

## 它们怎么咬合

```text
[Slack] ── Agent 接入 ── Mohist Agent ── AgentJob + AgentSession
   │
[IDE / 其他 Agent host] ── 外部 Agent + Mohist Skill + mo ──┐
                                                              │
[Web UI / CLI] ── 直接使用 Mohist Agent 或领域命令 ─────────┤
                                                              ▼
                                                         [Project]
     │
     ├── [Epic] ────┐
     │     │ 多个 issue 组成 epic
     ▼     │
   [Issue] ◄────────┘
     │
     │  Workflow（默认 mohist/local）
     ▼
[Plan → Build → Check → Integrate → Done]
     │
     │  代码合并进你的仓库
     ▼
[产品向前进一步]
```

记住一个心智模型：**你通常留在已有工作场所；Mohist Agent 是可独立使用的代理人，Agent
接入只是把它带到 Slack；外部 Agent 也可以通过 Skill 使用 Mohist。Project 是产品与执行
边界；Epic 负责目标和供料；Issue 是工件；Workflow 是生产线；Inline Agent 直接执行
task；AgentSession 记录执行会话；Web UI 是备用操作和可视化平面。**

---

对应源码：领域分解见 [`design/domain-analysis.md`](../design/domain-analysis.md)。
