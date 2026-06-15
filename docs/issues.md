# Issue 管理

Issue 是你日常打交道的核心对象。这篇覆盖创建到关闭的所有操作。

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
mo issue create "Critical fix" --priority p0 --label bug --label urgent

# 指定 workflow profile
mo issue create "Quick typo fix" --workflow-profile quick-fix

# 指定 AI 模型
mo issue create "Complex refactor" --model claude-sonnet-4
```

### Issue body 怎么写

body 质量**决定 plan 质量**，plan 质量**决定整个 issue 的成败**。花 5 分钟写好 body，省 30 分钟纠正 plan。

一个好 body 包含：

```markdown
## Background
为什么做这个改动？遇到什么问题了？

## Goal
这个 issue 完成后，世界应该变成什么样？

## Non-goals
明确不做什么（避免 AI 顺手做太多）

## Acceptance criteria
怎么算完成？（可验证的条件）
```

**反例**（不要这样写）：

```
Add search
```

AI 不知道你要搜什么、搜哪些字段、要不要高亮、要不要分页。结果 plan 写一堆你不需要的东西。

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

- 当前 stage、health、approval 状态
- 完整 body 和 comments
- Workflow timeline
- Branch bar（当前分支状态）
- Diff / commits 概览
- Latest artifacts（plan/check 产物）
- 操作按钮（Start / Approve / Reject / Stop / Retry / 等）
- Coder sessions（AI 干活时的对话回放）

## 启动 Issue

```bash
mo issue start 42
```

启动后：

1. Mohist 创建 `mo/issue-42` worktree 分支
2. 进入 Plan 阶段
3. AI 开始干活

**前置条件**：
- Issue 在 backlog
- Runner 已连接（`mo status` 看 runner 状态）
- 没超过并发上限（默认 8）

## 等审批时

Plan / Check 完成后，issue 进入 `awaiting approval`。这时 AI 不工作，等你决定：

```bash
mo issue approve 42     # 通过，进下一阶段
mo issue reject 42      # 打回，AI 重做当前阶段
```

**Reject 时的反馈**：reject 命令当前不带理由（roadmap）。如果你想让 AI 知道为什么 reject，先 add comment 再 reject：

```bash
mo issue comment 42 "Reject because: missing error handling in proposal"
mo issue reject 42
```

## Comment（评论）

```bash
# 加评论
mo issue comment 42 "Looks good but check edge cases"

# 删除评论（需要 comment id，从 mo issue show 拿）
mo issue comment-delete 42 <comment-id>
```

Web UI 上 issue 详情页底部有 comment 区。

Comment 是你和 AI 协作的**轻量通道**——AI 在 plan 阶段会读 comment 作为额外上下文。

## Prerequisite（前置依赖）

"等 #10 完成再开始 #11"：

```bash
mo issue prerequisite-add 11 10    # #11 等 #10
mo issue prerequisite-remove 11 10 # 移除依赖
```

有 prerequisite 的 issue，启动时会检查依赖是否完成。没完成就拒绝启动。

Web UI 上 issue 详情页有 "Add Prerequisite" 区。

## 暂停与停止

```bash
# 软暂停（stop）—— workflow 状态保留，可以 resume
mo issue stop 42

# 强制停止（force-stop）—— 终止运行中的 agent session
mo issue force-stop 42

# 完全关闭（close）—— issue 进入终态
mo issue close 42

# 重新打开（reopen）—— 已关闭的 issue 重新激活
mo issue reopen 42
```

| 操作 | 适用场景 | 后果 |
|---|---|---|
| `stop` | 暂时不想让它跑 | 状态保留，可 resume |
| `force-stop` | agent 卡死、stop 不响应 | 强杀 agent，状态可能 interrupted |
| `close` | 这个 issue 不做了 | 进入 closed 终态，可 reopen |
| `reopen` | 误关了，或想再做 | 回到 backlog |

## 恢复（失败后怎么办）

失败恢复是 Mohist 的强项。看 [故障恢复](troubleshooting.md) 详解。这里给速查：

| 场景 | 命令 |
|---|---|
| Issue blocked，想重试当前阶段 | `mo issue retry 42` |
| Issue interrupted（进程崩了），从断点继续 | `mo issue resume 42` |
| 想完全重做当前阶段（丢弃产物） | `mo issue rerun 42` |
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
mo issue update 42 --label bug --label critical
```

启动后的 issue 改 body 要谨慎——AI 已经基于旧 body 在工作。

## CLI 完整命令一览

```
mo issue create <title> [options]
mo issue list [options]
mo issue show <number>
mo issue update <number> [options]
mo issue start <number>
mo issue approve <number>
mo issue reject <number>
mo issue close <number>
mo issue reopen <number>
mo issue retry <number>
mo issue rerun <number>
mo issue force-stop <number>
mo issue resume <number>
mo issue stop <number>
mo issue rebase <number>
mo issue archive <number>
mo issue unarchive <number>
mo issue comment <number> <body>
mo issue prerequisite-add <number> <prereq-number>
mo issue prerequisite-remove <number> <prereq-number>
mo issue logs <number>
mo issue events <number>
mo issue diff <number>
mo issue commits <number>
mo issue sessions <number>     # coder session 回放
mo issue workflow [options]    # workflow 子命令
```

完整选项看 `mo issue <command> --help`。
