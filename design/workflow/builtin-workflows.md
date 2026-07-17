# 内置 Workflow

内容真源是 `packages/server/src/Mohist.Server/Workflow/Services/Profiles/` 下的
`*.workflow.yaml`。`mohist/*` Profile 出现在每个 Project 的 WorkflowProfile collection
中，但 definition 由当前 Mohist 版本管理，不复制成 Project 可编辑的数据。升级 Mohist
会更新这些 Profile，并只影响之后创建的 WorkflowRun；进行中的 Run 继续使用自己的
Definition snapshot。

内置 Profile 不允许修改或删除。需要定制时，创建新的 Project Profile。本篇只记录设计
取舍与不变量，不复述 yaml。

- `mohist/local` —— 本地 rebase --squash 后直接 push 到 base branch。默认。
- `mohist/github-pr` —— draft PR → ready → squash merge，经 GitHub PR 交付。

选择方式：

```bash
mo issue create "..." --workflow-profile mohist/github-pr
```

## 共享骨架

两个 workflow 共享同一条主干：

```
plan → approval → build → check → approval → integrate（sequential，project-integration 锁）
```

- 每个 stage 以 `workspace-prepare` 开头，task 总在就位的 workspace 上执行。
- **plan**：`proposal → specs → design → tasks → self-review`。`self-review` 用 `expect.markers` 声明 `<promise>PASS/FAIL</promise>`，`failIf` 把 FAIL 映射成 task 失败；recovery handler `when: promise=FAIL` 触发修复 task 后 `retrySelf`。stage check `plan-artifacts`（`mohist/openspec-artifacts`）验证 openspec 产物齐全。
- **build**：`load-tasks`（`mohist/openspec-tasks`）按 `tasks.json` 展开子 task，prompt 由 `mohist/openspec-task-prompt` 组合；`verify` 跑 `vars.ci.verify`，`errorCode=script-failed` → 修复 agent → `retrySelf`。
- **check**：`ai-review` 复用与 `self-review` 相同的 promise-marker + recovery 模式。
- **审批反馈**：profile 顶层 `approval.feedback.task` 声明 `apply-feedback`，session 取被驳回 stage 的同名 session，反馈修复延续该 stage 的上下文。
- **rebase 冲突统一走 task-level recovery**：`mohist/rebase` 冲突时返回 `errorCode: conflict` 并保留 rebase 进行中，嵌套 handler 派 agent 解冲突并完成 rebase（该 handler 不 `retrySelf`，agent 自己收尾）。恢复 prompt 用命名模板引用（如 `${{ prompts.resolve-rebase-conflicts }}`），模板可访问 `${{ failure.output }}`。

Recovery 机制本身见 [`recovery.md`](recovery.md)，action 契约见 [`actions.md`](actions.md)。

## mohist/local

最短交付路径：不开 PR、不依赖 GitHub。

- **check** 比 github-pr 多一个 `merge-ready` task：进入审批前确认分支可合入 base，`canMerge=false` 时先 rebase onto base（内嵌 conflict 恢复）再 `retrySelf`。设计意图：审批通过时分支已经可合，integrate 不再因分支落后而失败。
- **integrate**：`archive-change`（`errorCode=retry-safe` 直接 `retrySelf`）→ `rebase --squash`（commit message 取 `issue.title`）→ `push` 到 base branch。
- 各 stage 带 `git diff --check` 的 health task（plan 里是 stage check），`errorCode=script-failed` 时由 agent 只修 whitespace / patch 格式问题后 `retrySelf`。

## mohist/github-pr

经 GitHub PR 交付：plan 结束时开 draft PR，check 审批后标记 ready，integrate 时 squash merge。要求 runner host 装有 `gh` CLI 且已对目标仓库 `gh auth login`。

### PR 身份与元数据

- `open-draft-pr` 是 plan 的最后一个 task：创建或复用 draft PR，`setVars` 把 `output.prNumber` / `output.prUrl` 写入 `vars.github.pr.{number,url}`。PR 身份进 workflow runtime variables，后续 stage 只读引用，不重复开 PR。
- PR title/body 不从 workflow metadata 读取：`titleFrom: issue.title`、`bodyFrom: issue.body` 指示 `mohist/create-github-pr` 在运行时取 issue 数据创建或更新 PR。

### Check 与 Integrate

- **check**：`ai-review` 通过后 `push`（`forceWithLease: true`，允许 rebase 后 head history 重写）→ `mark-pr-ready`（幂等：只读 `vars.github.pr.number`，PR 已 ready 时直接成功；不更新 title/body、不推代码）。stage check `github-pr-status` 只读确认 PR 状态。
- **integrate**：`archive-change` → `push` → `merge-pr`（`mohist/merge-github-pr`：等待 GitHub PR checks，squash merge，重新查询确认 `state=MERGED`）。stage check `merge-verified` 用 `github-pr-status` 的 `expect: merged` 做只读确认。

### merge-pr 的恢复

`mohist/merge-github-pr` 用 action-owned `errorCode` 表达 recoverable failure，全部由 profile 在 `merge-pr.recovery` 显式声明，不靠 stage hook 或隐式边界动作：

- `errorCode=base-moved` → `recover:rebase`（`squash: false`，内嵌 conflict → agent 解冲突）→ `recover:push`（force）→ `retrySelf`。
- `errorCode=pr-checks-failed` → `recover:fix-pr-checks`（agent 修失败的 checks）→ `recover:push`（forceWithLease）→ `retrySelf`。
- `errorCode=protection-conflict` → 直接 `retrySelf`。

### 不变量

- PR checks 是 merge action 的内部前置条件，不是 stage check。
- 所有 PR 副作用都是显式 task，没有隐式 stage 边界钩子。
- `push` 不声明业务 recovery：push 失败意味着权限/网络问题或远程 branch 被外部写入，应作为普通 task failure 暴露。
- 恢复 agent 的职责边界：`recover:resolve-rebase-conflicts` 解冲突并完成 rebase；`recover:fix-pr-checks` 只修 checks——push 一律由后续显式 `recover:push` 承担。

所有 agent task 都使用 `mohist/opencode` 与 `options: ${{ vars.agent }}`；`expect` 是
task-level 完成契约，存放在 `with` / `artifacts` / `setVars` / `recovery` 同一层；approval
feedback 的 `apply-feedback` 显式绑定 `options`，尊重 issue 级模型选择。完整的 Action
契约见 [`actions.md`](actions.md)。
