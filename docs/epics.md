# 用 Epic 规划

Epic 是把零散 issue 组织成产品目标的工具，也是给生产线持续供料的单位。它决定哪些 issue 属于同一个目标，以及当前哪个 issue 可以交给 workflow 推进。

## 什么时候用 Epic

**用**：

- 一个产品目标需要 3+ 个 issue 才能完成（如"加上完整登录系统"）
- 你想做 roadmap 规划，知道下个月做哪几件事
- 想看一个目标的整体进度，而不是只看单个 issue
- 想让一个目标下的 ready issue 自动接续推进

**不用**：

- 单个独立的小改动
- 还没想清楚的目标（先 backlog 里堆着）

## 创建 Epic

### CLI（推荐）

```bash
mo epic create "Add user authentication" \
  --description "完整登录系统：注册、登录、密码重置、session 管理" \
  --priority p1 \
  --project <project-name-or-id>
```

`--description` / `-d` 接收长 markdown（推荐先写进文件再传入）；`--priority` 用 `p0`–`p4`。

### Web UI

Epics 页（顶部导航）→ **New Epic**。

### Epic 的属性

| 属性 | 含义 |
|---|---|
| 标题 | 短标题 |
| 描述 | 长描述。建议写：Goal、Background、Non-goals、包含哪些 issue |
| 优先级 | p0–p4 |
| 状态 | idle / running / paused / done / closed（由生命周期管理） |

Epic 和 Issue 都用 Project 内的编号作为永久身份。命令、页面和事件使用同一个编号，
不再要求用户在编号之外理解另一套 id。

**好的 epic 描述示例**：

```markdown
## Goal
让用户能注册、登录、找回密码。

## Background
产品当前无身份系统，所有 API 都是公开的。需要先加身份才能做个性化功能。

## Non-goals
- 不做 OAuth（先用 email/password）
- 不做 RBAC（先单一 admin 角色）
- 不做 2FA

## 包含
- 注册（email + password）
- 登录 / 登出
- 密码重置（邮件链接）
- Session 管理（JWT）
- 保护 API 中间件
```

## 把 Issue 关联到 Epic

### CLI（推荐）

```bash
mo epic link <epic-number> <issue-number>
mo epic unlink <epic-number> <issue-number>
```

关联会把 Issue 的当前 Epic 改为指定 Epic；若它原本属于另一个 Epic，则直接完成迁移。
取消关联只在 Issue 当前属于指定 Epic 时生效。重复执行同一操作是安全的。

### Web UI

issue 详情页 → **Edit** → 选 Epic；或在 Epic 详情页的 Linked Issues 列表里添加 / 移除。

一个 Issue 同一时刻最多属于一个 Epic。这个归属是 Issue 自身的一部分；Epic 展示的成员、
进度和下一个待推进 Issue 都由各 Issue 的当前归属汇总得出。

`closed` Epic 拒绝新关联，必须先 Reopen。向 `done` Epic 关联 open Issue 时，Epic 会
恢复为 `running`；关联终态 Issue 不会唤醒 Epic。

## 查看 Epic

### Web UI

- **Epics 列表页**：所有 epic 概览，按状态分组，显示每个 epic 的当前状态和下一个待推进 issue
- **Epic 详情页**：epic 信息 + 关联的 issue 列表 + 进度（已交付数 / 总数）+ 当前状态与下一步

### CLI

```bash
# 列出所有 epic
mo epic list --project <project>

# 显示详情（使用 Project 内的 Epic 编号）
mo epic show <epic-number> --project <project>
```

详情（Web UI 详情页或 `mo epic show`）会展示 Epic 的进度：已交付了几个 issue、总共几个、几个被 blocked、几个正在进行；下一个待推进的 issue 是哪一个、当前为什么没有推进；以及是否已经满足标记完成的条件。

## Epic 的生命周期

Epic 有五个生命周期状态，由用户操作和自动推进共同驱动。

| 状态 | 含义 | 进入条件 |
|---|---|---|
| `idle` | 已创建，但未开始自动推进 | 创建后默认 |
| `running` | 正在自动推进 linked issues | 从 `idle` 执行 Start |
| `paused` | 暂停自动推进，当前 in-progress issue 不中断 | 从 `running` 执行 Pause |
| `done` | 当前已完成（没有 open linked issues） | 在非 `paused`、非 `closed` 状态下执行 Mark Done，且所有 linked issues 都已进入终态；或系统重新计算进度时发现符合条件的非 `paused`、非 `closed` Epic 已没有 open linked issues，自动转入 `done` |
| `closed` | 关闭（不再继续） | 从 `idle`、`running` 或 `paused` 执行 Close |

- **新建 Epic 默认为 `idle`**，不会自动开始推进。必须显式 Start 才会进入 `running`。
- **`done` 和 `closed` 是完成状态**，只有 Reopen 能显式恢复为 `idle`；此外，`done` 在关联新的 open Issue 后会自动恢复为 `running`。`closed` 不接受新关联。

### Start / Pause / Resume

| 操作 | CLI | Web UI | 语义 |
|---|---|---|---|
| Start | `mo epic start <number>` | **Start Epic** | 将 idle → running，并尝试推进第一个 startable linked issue |
| Pause | `mo epic pause <number>` | **Pause** | 将 running → paused，停止未来推进，不中断当前 in-progress issue |
| Resume | `mo epic resume <number>` | **Resume** | 将 paused → running，重新评估 readiness 并推进 |

**重复执行是安全的**：对已处在目标状态的 Epic 重复执行对应操作不报错、无副作用（例如对已是 `running` 的 epic 执行 Start）；在其他不匹配的状态下执行会被拒绝并提示当前状态。

```bash
# Start（idle → running，同时尝试启动第一个 linked issue）
mo epic start 12

# Pause（running → paused，不中断当前 issue）
mo epic pause 12

# Resume（paused → running，重新开始推进）
mo epic resume 12
```

### 自动推进与 running-but-idle

`running` 的 Epic 会在当前 in-progress linked issue 到达终态（`done` / `cancelled`）后，**自动推进到下一个 startable issue**。`idle` 和 `paused` 状态的 Epic **不会自动推进**。

这不是批量启动。Epic 每次把当前可推进的 issue 交给 workflow，避免一个目标下的工作全靠 owner 手动接力。

当一个 `running` 的 Epic 仍有 open linked issue、但没有可推进的 next startable issue 时，它处于 **running-but-idle** 的可观察情况。此时 Epic 仍然是 `running` 状态（**不是第六个状态**），Epic 详情（Web UI 详情页或 `mo epic show`）会解释当前为什么没有推进（例如正在等待某个 in-progress issue 完成、下一个 issue 被 blocked 或依赖未就绪）。

没有 linked issues 时，详情页会提示这是一个空 Epic；所有 linked issues 都已进入终态时，详情会显示已可标记完成，并可能由系统自动转为 `done`。

#### 何时不会推进

- Epic 不是 `running` 状态
- 没有 linked issues
- 所有 linked issues 已是终态
- 下一个 issue 不满足 startable 条件（例如被 blocked 或依赖未就绪）

### Mark done / Close

```bash
# Mark done（前置条件：非 paused/closed，且没有 open linked issues）
mo epic done <epic-number>

# Close（关闭，不再继续）
mo epic close <epic-number>

# Reopen（done / closed → idle）
mo epic reopen <epic-number>
```

Web UI 上对应 Epic 详情页的 **Mark Done** / **Close Epic** / **Reopen** 按钮。

除了手动 Mark Done，系统在重新计算 linked issues 终态后，也会把符合条件的非 `paused`、非 `closed` Epic 自动转为 `done`。这表示你观察到的完成结果，不是一个需要额外触发的用户操作。

进度中的已交付数只统计 done 的 issue；cancelled issue 是终态、满足完成条件，但不计入已交付。

## 推荐工作流

1. 建 Epic，描述里写 Goal / Background / Non-goals（粗略即可），默认 `idle`。
2. 在 Epic 下逐步创建 / link issue，每个 issue 一个清晰可交付的功能点。
3. `mo epic start` 开始自动推进；`pause` / `resume` 随时调整。
4. 没有 open linked issues 时 `mo epic done`；要重新规划就 `reopen` 再 Start。

## 和 workflow 的关系

Epic 会**影响 linked issues 的推进**（决定何时自动启动下一个 issue），但**不改变每个 issue 自身 workflow 的执行规则**。

每个 linked issue 仍然走自己的 workflow（默认 `mohist/local`，或你 per-issue 指定）。Epic 决定的是"什么时候把下一个 issue 交给 workflow 去执行"，而不是"workflow 里有哪些步骤"。

## 和子 issue 的关系

Epic 与复合 issue（[复合 Issue 与子 Issue](sub-issues.md)）是两个正交的组织轴：Epic 组织**产品目标下的多个交付物**，复合 issue 是**一份工作的内部分工**。边界规则：

- **子 issue 不能 link 到 Epic**，Epic 的自动推进永远不会触碰子 issue。
- **父 issue 是普通的 Epic 成员**：轮到它时 Epic 启动它（父 issue 的启动即推进其子 issue），它 done 时计入 Epic 进度。Epic 不感知复合结构，本节不改变 Epic 的任何行为。

**选择指引**：各部分是独立有价值的交付物 → Epic + 普通 issue；各部分只是同一份需求的分工（完成一半没有产品意义）→ 复合 issue。

## 实装差距

当前版本的部分 Epic 命令仍接受内部 id，CLI 尚未提供 Reopen，并且关联关系尚未完全
收敛为 Issue 的单一归属。目标模型由 issue #412 推进；正文描述的是完成后的产品行为。

## 当前限制

Roadmap（已知不足）：

- 没有 roadmap 时间线视图（只有列表）
- Epic 不能嵌套
- 没有 epic 间的依赖图
- 不能批量启动 epic 内所有 backlog issue

---

对应源码：`packages/server/src/Mohist.Server/Epic/`、`Api/EpicRoutes.cs`。
