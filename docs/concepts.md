# 核心概念

理解这五个概念，就能理解 Mohist 的全部。

## 一句话版本

> 你在 **Project** 里建 **Issue**，Issue 按 **Workflow** 跑到 Done。多个相关 Issue 组成 **Epic**。需求探索由外部 **Skill** 完成，产物是 Issue。

## Project（项目）

一个 Project 对应你手上的一个真实代码仓库。

- 每个 Project 关联一个 git 仓库（path + base branch）
- Project 内的所有 issue 共享这个仓库
- 同时只能有一个 active project（CLI 用 `mo use` 切换）
- 不同 Project 的数据完全隔离

```bash
mo project create my-app --path /path/to/repo
mo use my-app
mo status   # 查看当前 project
```

**多个 Project 的场景**：你有 side project A、side project B，分别建 Project，按需切换。

## Issue（工作单元）

一个 Issue = 一份要 AI 完成的工作。

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
| `health` | active / paused / blocked / interrupted / cancelled / done（运行健康度） |
| `approvalState` | 当前是否在等你审批 |

详见 [Issue 管理](issues.md)。

## Workflow（工作流）

Workflow 是 Issue 从 idea 走到 merged 的固定流程。Mohist 的默认 workflow 有 5 个阶段：

```
Draft → Plan → Build → Check → Integrate → Done
         ↑         |          |
         |         v          v
       Backlog   Build     Build
                (rejected) (integrate failed)
```

每个阶段：

- **Draft** — Issue 创建后的初始状态，没启动
- **Plan** — AI 理解需求、产出 proposal/design/specs/tasks
- **Build** — AI 按 tasks 写代码、跑测试
- **Check** — AI review 自己的产出
- **Integrate** — 合并分支回 base branch

某些阶段（默认是 Plan 和 Check）完成后会**等你审批**，不批准不往下走。

详见 [工作流详解](the-workflow.md)。

### Workflow Profile

Workflow 不是写死的。你可以定义多个 **Workflow Profile**，每个 issue 选一个用。

比如：
- `mohist/default` — 完整 5 阶段流程（默认）
- `quick-fix` — 简化流程，跳过 design 适合小改动
- `experiment` — 全自动不审批，适合试验性改动

详见 [Workflow Profile](workflow-profiles.md)。

## Epic（产品里程碑）

Epic 是一组相关 Issue 的集合，代表一个产品目标。

典型用法：

- Epic: "Add user authentication" → 包含 #12 (login)、#13 (signup)、#14 (password reset)
- Epic: "Performance pass" → 包含 #20 (DB index)、#21 (cache layer)、#22 (lazy load)

Epic 让你做产品规划，而不是只响应 issue 流。

详见 [用 Epic 规划](epics.md)。

## Skill（外部 agent 能力）

Mohist 不内置对话式探索。需求挖掘、产品思考这类**需要实时互动**的工作，由外部 agent（OpenCode、Claude Code 等）通过 **Skill** 完成。

Mohist 分发两个 Skill：

| Skill | 作用 |
|---|---|
| `mohist` | 在外部 agent 里操作 Mohist（创建/审批 issue、查状态） |
| `mohist-explore` | 从产品视角探索需求，产出结构化 issue |

工作流：

```
你在 OpenCode/Claude Code 里探索需求
  ↓（mohist-explore skill 引导）
产出结构化 issue body
  ↓（mohist skill 创建 issue）
Issue 进入 Mohist workflow
  ↓
AI 自治执行到 Done
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
     │  Workflow（默认 mohist/default）
     ▼
[Plan → Build → Check → Integrate → Done]
     │
     │  代码合并进你的仓库
     ▼
[产品向前进一步]
```

记住一个心智模型：**你是 owner，AI 是开发团队，Workflow 是工作流，Epic 是路线图，Skill 是你的需求分析助手。**
