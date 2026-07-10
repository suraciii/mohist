# 核心概念

理解这些概念，就能理解 Mohist 怎么把工作持续推到 Done。

## 一句话版本

> 你在 **Project** 里把产品目标拆成 **Issue**，由 **Workflow** 推进到 Done。多个相关 Issue 组成 **Epic**。**Agent** 执行工作，也可以通过事件订阅响应系统事件。需求探索由外部 **Skill** 完成，产物是可进入 Workflow 的 Issue。

## Project（项目）

一个 Project 对应你手上的一个真实代码仓库。

- 每个 Project 关联一个 git 仓库（path + base branch）
- Project 内的所有 issue 共享这个仓库
- 同时只能有一个 active project（CLI 用 `mo project use` 切换）
- 不同 Project 的数据完全隔离

```bash
mo project create my-app --path /path/to/repo
mo project use my-app
mo project status   # 查看当前 project
```

**多个 Project 的场景**：你有 side project A、side project B，分别建 Project，按需切换。

## Issue（工作单元）

一个 Issue = 一份可进入生产线的工作。

- 标题 + body（描述需求）
- 优先级 p0（最高）~ p4（最低）
- 标签（自由文本）
- 进入 workflow 后会有 stage、health、approvalState 等状态
- 完成后留下一整套 OpenSpec 产物（proposal / design / specs / tasks / review）

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
- **Plan** — Agent 理解需求、产出 proposal/design/specs/tasks
- **Build** — Agent 按 tasks 写代码、跑测试
- **Check** — Agent review 自己的产出
- **Integrate** — 合并分支回 base branch

某些阶段（默认是 Plan 和 Check）完成后会进入**审批点**。Workflow 只关心是否收到 approve / reject 决策，不关心审批者是 owner、Agent 还是脚本。

详见 [工作流详解](the-workflow.md)。

### Workflow Profile

Workflow 不是写死的。产品模型支持多个 **Workflow Profile**，每个 issue 可以选一个用。

当前内置 profile：
- `mohist/local` — 完整 5 阶段流程（默认）

详见 [Workflow Profile](workflow-profiles.md)。

## Epic（产品目标）

Epic 是为一个产品目标持续供料的单位。新建的 Epic 默认 `idle`，通过 **Start** 启动自动推进——Epic 会在 linked issue 完成后启动下一个可推进的 issue。

详见 [用 Epic 规划](epics.md) 了解完整生命周期。

## Agent（执行者、响应者、代理人）

Agent 有两种用法：

- **执行 workflow task**：Runner 启动 Agent，让它按 workflow 输入完成规划、实现、审查、修复等任务。
- **响应系统事件**：Agent 事件订阅让 Agent 监听 workflow / issue / epic / runner 等事件，按订阅提示词自动行动。

Agent 的核心位置是代理人：它进入流水线上原本由 owner 负责的占位。所有 Agent 可以做的动作，都必须能由人手动做；Agent 只是按配置和提示词代你执行。

事件响应不是只为审批准备的。常见场景包括在审批点到达时发起 approve / reject、分析失败、总结完成内容、生成后续 issue、通知 owner。

## Approval（审批）

审批是对 workflow 阶段产物的 approve / reject 决策。阶段完成后可以进入审批点，等待审批者决定产物能否继续流动。审批不限定审批者必须是人，它是流水线上的一个角色占位。

一个审批点可以由多种机制给出决策：

- 自动检查：测试、lint、artifact 是否齐全。
- Agent 或脚本：读取证据后调用 approve / reject。
- owner：在需要人工判断时调用 approve / reject。

Workflow 不区分这些来源。谁来发起审批，是 Agent、CLI、Web UI 或外部自动化的职责；审批结果仍然只有 approve / reject。

## Skill（外部 agent 能力）

Mohist 不内置对话式探索。需求挖掘、产品思考这类**需要实时互动**的工作，由外部 agent（OpenCode、Claude Code 等）通过 **Skill** 完成。

Mohist 分发两个 Skill：

| Skill | 作用 |
|---|---|
| `mohist` | 在外部 agent 里操作 Mohist（创建 issue、审批、查状态） |
| `mohist-explore` | 从产品视角探索需求，产出可进入 workflow 的结构化 issue |

工作流：

```
你在 OpenCode/Claude Code 里探索需求
  ↓（mohist-explore skill 引导）
产出结构化 issue body
  ↓（mohist skill 创建 issue）
Issue 进入 Mohist workflow
  ↓
Agent 按 workflow 执行到 Done
```

详见 [Skill 机制](skills.md)。

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

记住一个心智模型：**你是 owner；Epic 负责目标和供料；Issue 是工件；Workflow 是生产线；Agent 是代理人；审批点决定工件能不能继续流动。**

---

对应源码：领域分解见 [`design/domain-analysis.md`](../design/domain-analysis.md)。
