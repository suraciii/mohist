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

`--description` / `-d` 接收长 markdown（推荐先写进文件再传入）；`--priority` 用 `p0`–`p3`。

### Web UI

Epics 页（顶部导航）→ **New Epic**。

### API

也可直接调 API：

```bash
curl -X POST http://localhost:3456/api/projects/<project>/epics \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Add user authentication",
    "description": "完整登录系统：注册、登录、密码重置、session 管理",
    "priority": "p1",
    "projectId": "<project-id>"
  }'
```

### Epic 的字段

| 字段 | 含义 |
|---|---|
| `title` | 短标题 |
| `description` | 长描述。建议写：Goal、Background、Non-goals、包含哪些 issue |
| `priority` | p0-p4 |
| `status` | idle / running / paused / done / closed（由生命周期管理） |

**好的 epic description 示例**：

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
mo epic link <epic-id-or-number> <issue-id-or-number>
mo epic unlink <epic-id-or-number> <issue-id>
```

两者都接受 id 或 number。约束：一个 issue 只能属于一个 primary epic，重复关联会报 `DUPLICATE_EPIC_MEMBERSHIP`。

### 其他方式

Web UI 上 issue 详情页 → Edit → 选 Epic；或直接调 API：

```bash
curl -X POST http://localhost:3456/api/projects/<project>/epics/<epic-id>/issues \
  -H "Content-Type: application/json" \
  -d '{"issueId": "42"}'
```

一个 issue 只能属于一个 epic（primary epic）。

## 查看 Epic

### Web UI

- **Epics 列表页**：所有 epic 概览，按状态分组，显示每个 epic 的当前状态和下一个待推进 issue
- **Epic 详情页**：epic 信息 + 关联的 issue 列表 + 进度（X/Y delivered）+ 当前状态与下一步

### CLI

```bash
# 列出所有 epic
mo epic list --project <project>

# 显示详情（用 epic id 或 number）
mo epic show <epic-id-or-number> --project <project>
```

### API

```bash
# 列出所有 epic
curl http://localhost:3456/api/projects/<project>/epics

# 详情（用 epic id 或 number）
curl http://localhost:3456/api/projects/<project>/epics/<epic-id>
curl http://localhost:3456/api/projects/<project>/epics/1
```

详情返回里包含 `progress` 字段：

```json
{
  "progress": {
    "deliveredCount": 2,
    "totalIssueCount": 5,
    "blockedIssues": 1,
    "activeIssues": 2,
    "nextIssue": { ... },
    "nextIssueReason": "...",
    "readyToMarkDone": false
  }
}
```

## Epic 的生命周期

Epic 有五个生命周期状态，由用户操作和自动推进共同驱动。

| 状态 | 含义 | 进入条件 |
|---|---|---|
| `idle` | 已创建，但未开始自动推进 | 创建后默认 |
| `running` | 正在自动推进 linked issues | 从 `idle` 执行 Start |
| `paused` | 暂停自动推进，当前 in-progress issue 不中断 | 从 `running` 执行 Pause |
| `done` | 完成（没有 open linked issues） | 在非 `paused`、非 terminal 状态下执行 Mark Done，且所有 linked issues 都已进入终态；或系统重新计算进度时发现符合条件的非 `paused`、非 terminal Epic 已没有 open linked issues，自动转入 `done` |
| `closed` | 关闭（不再继续） | 任意状态（非 terminal）执行 Close |

- **新建 Epic 默认为 `idle`**，不会自动开始推进。必须显式 Start 才会进入 `running`。
- **`done` 和 `closed` 是终态**，进入后不能再切换为其他状态。

### Start / Pause / Resume

| 操作 | CLI | Web UI | HTTP API | 语义 |
|---|---|---|---|---|
| Start | `mo epic start <id>` | **Start Epic** | `POST /api/projects/{project}/epics/{id}/start` | 将 idle → running，并尝试推进第一个 startable linked issue |
| Pause | `mo epic pause <id>` | **Pause** | `POST /api/projects/{project}/epics/{id}/pause` | 将 running → paused，停止未来推进，不中断当前 in-progress issue |
| Resume | `mo epic resume <id>` | **Resume** | `POST /api/projects/{project}/epics/{id}/resume` | 将 paused → running，重新评估 readiness 并推进 |

**Idempotency**：重复执行当前状态对应的操作不报错（例如对已是 `running` 的 epic 执行 Start 是 no-op）。每个操作在非预期状态返回冲突错误。

#### CLI 示例

```bash
# Start（idle → running，同时尝试启动第一个 linked issue）
mo epic start my-epic-id

# Pause（running → paused，不中断当前 issue）
mo epic pause my-epic-id

# Resume（paused → running，重新开始推进）
mo epic resume my-epic-id
```

#### API 示例

```bash
# Start
curl -X POST http://localhost:3456/api/projects/<project>/epics/<epic-id>/start

# Pause（可选附带原因）
curl -X POST http://localhost:3456/api/projects/<project>/epics/<epic-id>/pause \
  -H "Content-Type: application/json" \
  -d '{"reason": "等待设计评审"}'

# Resume
curl -X POST http://localhost:3456/api/projects/<project>/epics/<epic-id>/resume
```

### 自动推进与 running-but-idle

`running` 的 Epic 会在当前 in-progress linked issue 到达终态（`done` / `cancelled`）后，**自动推进到下一个 startable issue**。`idle` 和 `paused` 状态的 Epic **不会自动推进**。

这不是批量启动。Epic 每次把当前可推进的 issue 交给 workflow，避免一个目标下的工作全靠 owner 手动接力。

当一个 `running` 的 Epic 仍有 open linked issue、但没有可推进的 next startable issue 时，它处于 **running-but-idle** 的可观察情况。此时 Epic 仍然是 `running` 状态（**不是第六个状态**），`progress.nextIssueReason` 字段会解释当前为什么没有推进（例如正在等待某个 in-progress issue 完成、下一个 issue 被 blocked 或依赖未就绪）。

没有 linked issues 时，详情页会显示 empty-epic 信息；所有 linked issues 都已进入终态时，`readyToMarkDone` 会变为 true，并可能由系统自动转为 `done`。这两类情况不依赖 `nextIssueReason` 来解释。

#### 何时不会推进

- Epic 不是 `running` 状态
- 没有 linked issues
- 所有 linked issues 已是终态
- 下一个 issue 不满足 startable 条件（例如被 blocked 或依赖未就绪）

### Mark done / Close

```bash
# Mark done（前置条件：非 paused/terminal，且没有 open linked issues）
mo epic done <epic-id-or-number>
# curl -X POST http://localhost:3456/api/projects/<project>/epics/<epic-id>/done

# Close（关闭，不再继续）
mo epic close <epic-id-or-number>
# curl -X POST http://localhost:3456/api/projects/<project>/epics/<epic-id>/close
```

除了手动 Mark Done，系统在重新计算 linked issues 终态后，也会把符合条件的非 `paused`、非 terminal Epic 自动转为 `done`。这表示你观察到的完成结果，不是一个需要额外触发的用户操作。

## 推荐工作流

1. **想法出现时**：先建 Epic，description 写 Goal/Background/Non-goals（粗略即可）。新建的 Epic 默认 `idle`。
2. **细化时**：在 Epic 下逐步创建 / link issue（每个 issue 一个清晰可交付的功能点）。
3. **开始执行时**：`mo epic start <id>` 将 Epic 切换到 `running`。Epic 会自动推进第一个 startable linked issue。
4. **推进中**：当一个 linked issue 到达终态，`running` 的 Epic 自动推进到下一个 startable issue。你可以用 `mo epic show <id>` 查看 `progress.nextIssue` 了解下一步。
5. **需暂停时**：`mo epic pause <id>` 暂停推进，当前 issue 不受影响。
6. **恢复时**：`mo epic resume <id>` 恢复推进，Epic 重新评估并推进下一个 issue。
7. **完成时**：没有 open linked issues 时 `mo epic done <id>`。`deliveredCount` 仍只统计已 delivered 的 issue；cancelled issue 是终态，会满足完成 readiness，但不计入 delivered。

## 和 workflow 的关系

Epic 会**影响 linked issues 的推进**（决定何时自动启动下一个 issue），但**不改变每个 issue 自身 workflow 的执行规则**。

每个 linked issue 仍然走自己的 workflow（默认 `mohist/local`，或你 per-issue 指定）。Epic 决定的是"什么时候把下一个 issue 交给 workflow 去执行"，而不是"workflow 里有哪些步骤"。

## 和子 issue 的关系

Epic 与复合 issue（[复合 Issue 与子 Issue](sub-issues.md)）是两个正交的组织轴：Epic 组织**产品目标下的多个交付物**，复合 issue 是**一份工作的内部分工**。边界规则：

- **子 issue 不能 link 到 Epic**，Epic 的自动推进永远不会触碰子 issue。
- **父 issue 是普通的 Epic 成员**：轮到它时 Epic 启动它（父 issue 的启动即推进其子 issue），它 done 时计入 Epic 进度。Epic 不感知复合结构，本节不改变 Epic 的任何行为。

**选择指引**：各部分是独立有价值的交付物 → Epic + 普通 issue；各部分只是同一份需求的分工（完成一半没有产品意义）→ 复合 issue。

## 当前限制

Roadmap（已知不足）：

- 没有 roadmap 时间线视图（只有列表）
- Epic 不能嵌套
- 没有 epic 间的依赖图
- 不能批量启动 epic 内所有 backlog issue

如果你需要这些能力，欢迎贡献或提 issue。

---

对应源码：`packages/server/src/Mohist.Server/Epic/`、`Api/EpicRoutes.cs`。
