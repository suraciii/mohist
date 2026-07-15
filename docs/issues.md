# Issue 管理

Issue 是你日常打交道的核心对象。这篇覆盖创建到关闭的所有**操作**。各 workflow 阶段内部做什么、产出什么，见 [工作流详解](the-workflow.md)；把多个 issue 组织成里程碑，见 [用 Epic 规划](epics.md)。

## 创建 Issue

### Web UI

看板右上角 **New Issue** 按钮。填 title、body、priority、labels。

### CLI

```bash
# 最简
mo issue create "Add search feature"

# 带 body
mo issue create "Fix login bug" --body "Users can't login on Safari"

# 长 body 推荐用文件
mo issue create "Refactor auth module" --body-file ./issue-body.md

# 从 stdin
cat ./my-issue.md | mo issue create "My issue" --body-stdin

# 指定优先级和标签
mo issue create "Critical fix" --priority p0 --label kind=bug

# 指定 workflow profile
mo issue create "Implement search" --workflow-profile mohist/local

# 指定模型
mo issue create "Complex refactor" --model claude-sonnet-4

# 指定目标仓库（多仓库 project；不指定落 default 仓库）
mo issue create "server: 加订阅 API" --repo server

# 拆为某个 issue 的子 issue
mo issue create "web: 订阅管理页" --parent 42 --repo web
```

### 目标仓库与子 issue

- 每个 issue 有一个**目标仓库**，workflow 全程（分支、diff、Integrate）都发生在那里；启动后不可更改。详见 [仓库](repositories.md)。
- 一份需求横跨多个仓库时，拆成子 issue：父 issue 追踪整体，子 issue 各自走 workflow。详见 [复合 Issue 与子 Issue](sub-issues.md)。

### Issue body 怎么写

body 质量**决定 plan 质量**，plan 质量**决定整个 issue 的成败**。花 5 分钟写好 body，省 30 分钟纠正 plan。

一个好 body 包含：

```markdown
## Background
为什么做这个改动？遇到什么问题了？

## Goal
这个 issue 完成后，世界应该变成什么样？

## Non-goals
明确不做什么（避免 Inline Agent 顺手做太多）

## Acceptance criteria
怎么算完成？（可验证的条件）
```

**反例**（不要这样写）：

```
Add search
```

Inline Agent 不知道你要搜什么、搜哪些字段、要不要高亮、要不要分页。结果 plan 写一堆你不需要的东西。

**正例**：

```markdown
## Background
首页的任务列表已经 100+ 条，用户找不到旧任务。

## Goal
列表顶部加搜索框，按 title 模糊匹配，实时过滤。

## Non-goals
- 不搜索 description（暂不需要）
- 不做高级筛选
- 不改后端 API

## Acceptance criteria
- 输入 "foo" 时，只显示 title 包含 "foo" 的任务
- 大小写不敏感
- 空输入显示全部
- 渲染 200 条列表时无明显卡顿
```

## 查看 Issue

```bash
# 当前 project 所有 issue
mo issue list

# 只看某个 stage
mo issue list --stage plan

# 看已归档
mo issue list --archived

# 看所有（含归档）
mo issue list --all

# 详情
mo issue show 42
```

Web UI 上点 issue card 进详情页，能看到：

- 当前 stage、health、审批状态
- 完整 body 和 comments
- Workflow timeline
- Branch bar（当前分支状态）
- Diff / commits 概览
- Latest artifacts（plan/check 产物）
- 操作按钮（Start / Approve / Reject / Stop / Retry / 等）
- AgentSessions（Workflow 执行时的对话记录）

## 启动 Issue

```bash
mo issue start 42
```

启动后：

1. Mohist 创建 `mo/issue-42` worktree 分支
2. 进入 Plan 阶段
3. Inline Agent 开始执行

**前置条件**：
- Issue 在 backlog
- Runner 已连接（`mo project status` 看 runner 状态）
- 没超过并发上限（默认 8）

## 等待审批时

Plan / Check 完成后，issue 进入 `awaiting approval`。这表示 workflow 停在审批点，等待 approve / reject 决策：

```bash
mo issue approve 42     # 通过，进下一阶段
mo issue reject 42 --message "Missing error handling in proposal"  # 打回，Inline Agent 重做当前阶段
```

Workflow 不关心审批者是 owner、Mohist Agent 还是脚本。你手动处理时，`reject` 必须带理由，用 `--message`（或 `-m`）说明需要重做什么。需要更长上下文时，可以先 add comment，再用简短 reject message 指向它：

```bash
mo issue comment add 42 --body "Reject because: missing error handling in proposal"
mo issue reject 42 -m "See comment: missing error handling"
```

## Comment（评论）

```bash
# 加评论
mo issue comment add 42 --body "Looks good but check edge cases"

# 删除评论目前不提供 CLI 命令；使用 Web UI 或 API。
```

Web UI 上 issue 详情页底部有 comment 区。

Comment 是你和 Inline Agent 协作的**轻量通道**——Inline Agent 在 plan 阶段会读 comment 作为额外上下文。

## Prerequisite（前置依赖）

"等 #10 完成再开始 #11"：

```bash
mo issue prereq add 11 10    # #11 等 #10
mo issue prereq remove 11 10 # 移除依赖
```

有 prerequisite 的 issue，启动时会检查依赖是否完成。没完成就拒绝启动。

Web UI 上 issue 详情页有 "Add Prerequisite" 区。

## 中断、停止与关闭

```bash
# 可恢复暂停（force-stop）—— 终止当前执行回合，保留 AgentSession，后续用 resume 接着跑
mo issue force-stop 42

# 永久停止（stop）—— terminal，不能 resume
mo issue stop 42

# 完全关闭（close）—— issue 进入终态
mo issue close 42

# 重新打开（reopen）—— 已关闭的 issue 重新激活
mo issue reopen 42
```

| 操作 | 适用场景 | 后果 |
|---|---|---|
| `force-stop` | 暂时停止、Inline Agent 卡住、想保留恢复入口 | 终止当前回合，workflow 进入可 `resume` 的 paused 状态 |
| `stop` | 确定不再继续这次 workflow | 永久停止 workflow run，terminal，不能 resume |
| `close` | 这个 issue 不做了 | 进入 closed 终态，可 reopen |
| `reopen` | 误关了，或想再做 | 回到 backlog |

## 恢复（失败后怎么办）

失败恢复是 Mohist 的强项。看 [故障恢复](troubleshooting.md) 详解。这里给速查：

| 场景 | 命令 |
|---|---|
| Issue blocked，想重试当前阶段 | `mo issue retry 42` |
| Issue paused，继续当前 workflow | `mo issue resume 42` |
| 想完全重做当前阶段（丢弃产物） | `mo issue rerun 42` |
| 想从指定阶段重做（丢弃该阶段及之后产物） | `mo issue rerun 42 --from-stage build` |
| Base branch drift 了，rebase issue 分支 | `mo issue rebase 42` |

## 归档

Done 之后，issue 还会留在看板的 Done 列。归档后从看板移走：

```bash
mo issue archive 42
mo issue unarchive 42    # 反悔
mo issue list --archived  # 看归档列表
```

Web UI 上有 Archive 页。

## 修改 Issue

没启动的 issue 可以随便改：

```bash
mo issue update 42 --title "New title"
mo issue update 42 --body-file ./new-body.md
mo issue update 42 --priority p1
mo issue update 42 --label kind=bug --label area=web
```

启动后的 issue 改 body 要谨慎——Inline Agent 已经基于旧 body 在工作。

## CLI 完整命令一览

```
mo issue create <title> [--repo <仓库>] [--parent <编号>] [options]
mo issue list [--repo <仓库>] [--parent <编号>] [options]
mo issue show <number>
mo issue update <number> [options]
mo issue start <number>
mo issue approve <number>
mo issue reject <number> --message <message>
mo issue close <number>
mo issue reopen <number>
mo issue retry <number>
mo issue rerun <number>
mo issue rerun <number> --from-stage <stage>
mo issue force-stop <number>
mo issue resume <number>
mo issue stop <number>
mo issue rebase <number>
mo issue archive <number>
mo issue unarchive <number>
mo issue comment add <number> [--body <text>|--body-file <path>]
mo issue prereq add <number> <prereq-number>
mo issue prereq remove <number> <prereq-number>
mo issue logs <number>
mo issue events <number>
mo issue diff <number>
mo issue commits <number>
mo issue sessions <number>     # AgentSession 记录
mo issue workflow [options]    # workflow 子命令
```

完整选项看 `mo issue <command> --help`。

---

对应源码：`packages/server/src/Mohist.Server/Issue/`、`Api/IssueRoutes.*`；CLI `packages/cli/`。
