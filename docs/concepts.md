# 核心概念

理解这些概念，就能理解 Mohist 怎么把工作持续推到 Done。

## 一句话版本

> 你在 **Project** 里把产品目标拆成 **Issue**，由 **Workflow** 推进到 Done。多个相关 Issue 组成 **Epic**。Workflow 可通过 **Inline Agent** 直接执行 task；有稳定身份的 **Mohist Agent** 可以被启动，也可以由事件路由规则触发、响应系统事件。需求探索由外部 **Skill** 完成，产物是可进入 Workflow 的 Issue。

## Project（项目）

一个 Project 对应你手上的一个真实产品，是它的工作空间。

- 每个 Project 声明一个或多个 git **仓库**作为执行资源（每个仓库有资源名 + base branch），其中一个是 default 仓库
- Project 内的每个 issue 有一个**目标仓库**，不指定就落在 default 仓库
- 同时只能有一个 active project（CLI 用 `mo project use` 切换）
- 不同 Project 的数据完全隔离

```bash
mo project create my-app --path /path/to/repo   # --path 的仓库注册为 default 仓库
mo project use my-app
mo project status   # 查看当前 project
```

**多个 Project 的场景**：你有 side project A、side project B，分别建 Project，按需切换。

**多个仓库的场景**：产品的 server 和 web 是两个代码库，同一个 Project 声明两个仓库，issue 按目标仓库分流。详见 [仓库](repositories.md)。

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

Workflow 不是写死的。产品模型支持多个 **Workflow Profile**，每个 issue 可以选一个用。

当前内置 profile：
- `mohist/local` — 完整 5 阶段流程，本地合并（默认）
- `mohist/github-pr` — Integrate 阶段走 GitHub PR

详见 [Workflow Profile](workflow-profiles.md)。

## Epic（产品目标）

Epic 是为一个产品目标持续供料的单位。新建的 Epic 默认 `idle`，通过 **Start** 启动自动推进——Epic 会在 linked issue 完成后启动下一个可推进的 issue。

详见 [用 Epic 规划](epics.md) 了解完整生命周期。

## Inline Agent 与 Mohist Agent

Inline Agent 是 Workflow 直接通过 `mohist/opencode` 等 Action 调用 Agent 能力的
方式，没有独立 Agent ID。Mohist Agent 则是 Project 内有稳定 ID、名称、Instructions
和配置的 Named Agent，可以手动启动或响应事件。

AgentSession 不是 Agent，也不是工作结果；它记录一段对话的消息、上下文、用量和
会话沿革。Workflow TaskRun 或 AgentJob 负责工作生命周期，AgentSession 只负责
会话与审计。

完整关系见 [Agent 与 AgentSession](agents.md)；OpenCode Action 配置见
[`mohist/opencode` Action](actions/opencode.md)。

## Approval（审批）

审批是对 workflow 阶段产物的 approve / reject 决策。阶段完成后可以进入审批点，等待审批者决定产物能否继续流动。审批不限定审批者必须是人，它是流水线上的一个角色占位。

一个审批点可以由多种机制给出决策：

- 自动检查：测试、lint、artifact 是否齐全。
- Mohist Agent 或脚本：读取证据后调用 approve / reject。
- owner：在需要人工判断时调用 approve / reject。

Workflow 不区分这些来源。谁来发起审批，是 Mohist Agent、CLI、Web UI 或外部自动化的职责；审批结果仍然只有 approve / reject。

## Skill（外部 agent 能力）

Mohist 不内置对话式探索。需求挖掘、产品思考这类**需要实时互动**的工作，由外部 agent（OpenCode、Claude Code 等）通过 **Skill** 完成。

Mohist 分发两个 Skill：

| Skill | 作用 |
|---|---|
| `mohist` | 在外部 agent 里操作 Mohist（创建 issue、审批、查状态） |
| `mohist-explore` | 从产品视角探索需求，产出可进入 workflow 的结构化 issue |

典型路径：在外部 agent 里用 `mohist-explore` 探索需求、产出结构化 issue body，再用 `mohist` 创建 issue，进入 Mohist workflow 执行到 Done。完整工作流见 [Skill 机制](skills.md)。

## 它们怎么咬合

```
[你的产品想法]
     │
     │  Explore skill（在 OpenCode 里）
     ▼
[Epic] ────┐
     │     │ 多个 issue 组成 epic
     ▼     │
[Issue] ◄──┘
     │
     │  Workflow（默认 mohist/local）
     ▼
[Plan → Build → Check → Integrate → Done]
     │
     │  代码合并进你的仓库
     ▼
[产品向前进一步]
```

记住一个心智模型：**你是 owner；Epic 负责目标和供料；Issue 是工件；Workflow 是生产线；Inline Agent 直接执行 task；Mohist Agent 是可复用的代理人；AgentSession 记录每段对话；审批点决定工件能不能继续流动。**

---

对应源码：领域分解见 [`design/domain-analysis.md`](../design/domain-analysis.md)。
