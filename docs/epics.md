# 用 Epic 规划

Epic 是把零散 issue 组织成产品里程碑的工具。如果你只是被动响应 issue 流，你做的不是产品 owner，是消防员。

## 什么时候用 Epic

**用**：

- 一个产品目标需要 3+ 个 issue 才能完成（如"加上完整登录系统"）
- 你想做 roadmap 规划，知道下个月做哪几件事
- 想看一个目标的整体进度，而不是只看单个 issue

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
curl -X POST http://localhost:3456/api/projects/<your-project>/epics \
  -H "Content-Type: application/json" \
  -d '{
    "title": "Add user authentication",
    "description": "完整登录系统：注册、登录、密码重置、session 管理",
    "priority": "p1",
    "projectId": "<your-project-id>"
  }'
```

### Epic 的字段

| 字段 | 含义 |
|---|---|
| `title` | 短标题 |
| `description` | 长描述。建议写：Goal、Background、Non-goals、包含哪些 issue |
| `priority` | p0-p4 |
| `status` | active / done / closed（系统自动管理） |

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

- **Epics 列表页**：所有 epic 概览
- **Epic 详情页**：epic 信息 + 关联的 issue 列表 + 进度（X/Y delivered）

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
    "readyToMarkDone": false
  }
}
```

## Epic 的生命周期

| 状态 | 含义 | 进入条件 |
|---|---|---|
| `active` | 进行中 | 创建后默认 |
| `done` | 完成 | 所有 issue delivered，手动 mark done |
| `closed` | 关闭（不做了） | 手动 close |

Mark done：

```bash
mo epic done <epic-id-or-number>
# 或 API：
# curl -X POST http://localhost:3456/api/projects/<project>/epics/<epic-id>/done
```

Close：

```bash
mo epic close <epic-id-or-number>
# 或 API：
# curl -X POST http://localhost:3456/api/projects/<project>/epics/<epic-id>/close
```

## 推荐工作流

1. **想法出现时**：先建 Epic，description 写 Goal/Background/Non-goals（粗略即可）
2. **细化时**：在 Epic 下逐步创建 issue（每个 issue 一个清晰可交付的功能点）
3. **执行时**：按 priority 启动 epic 内的 issue
4. **完成时**：所有 issue delivered 后 mark epic done

## 和 workflow 的关系

Epic 不影响 workflow。Epic 内每个 issue 各自走自己的 workflow（默认 `mohist/local`，或你 per-issue 指定）。

Epic 只是组织工具，不参与执行。

## 当前限制

Roadmap（已知不足）：

- 没有 roadmap 时间线视图（只有列表）
- Epic 不能嵌套
- 没有 epic 间的依赖图
- 不能批量启动 epic 内所有 backlog issue

如果你需要这些能力，欢迎贡献或提 issue。

---

对应源码：`packages/server/src/Mohist.Server/Epic/`、`Api/EpicRoutes.cs`。
