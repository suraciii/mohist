---
purpose: "Mohist 内置 workflow：default 直推流与 PR 集成流。"
style: ["极简，只给目标态。"]
---

# Built-in Workflows

Mohist 内置两条系统 workflow：

- `mohist/default`：本地 squash 后直推 base branch。
- `mohist/pr`：通过 GitHub PR 交付并由 GitHub squash merge。

两条 workflow 的 plan/build/check 阶段保持一致，差异集中在 integrate 阶段。

## Common Stages

两条 workflow 都包含：

1. `plan`
   - proposal/specs/design/tasks/self-review
   - 需要人工审批
2. `build`
   - 执行 plan 产出的 tasks
   - 跑项目 verify
3. `check`
   - AI review
   - merge-ready
   - 需要人工审批
4. `integrate`
   - 串行锁 `project-integration`
   - 同步/归档 OpenSpec change
   - 交付代码

## mohist/default

目标：最短路径把已验证改动落到 base branch。

integrate：

```yaml
- integrate:spec-sync       # mohist/acp-agent
- integrate:archive-change  # mohist/archive-change
- integrate:rebase          # mohist/rebase, squash: true
- integrate:push            # mohist/push
```

语义：

- `integrate:rebase` fetch 最新 base，解决冲突，并把工作分支 squash 成一个 commit。
- squash commit message 由 `messageFrom: issue.title` 指示 `mohist/rebase` 在运行时通过 `mo issue show` 读取 issue title。
- `integrate:push` fast-forward 推送到 base branch。
- GitHub 上不产生 PR 记录。

适用：

- 小型 feature/bugfix。
- 本地个人项目快速集成。
- 不要求每次 merge 都在 GitHub 留 PR 记录。

## mohist/pr

目标：每次集成都通过一个可追踪的 GitHub PR 完成。PR 是 workflow
branch 到 base branch 的集成通道，PR 成功 merge 才表示集成完成。

profile 走 PR-first 形态：plan 批准后立刻通过显式 task 创建/复用 PR，
后续 stage 需要把最新工作推到 GitHub 时在该 stage 尾部追加显式 update
PR task，integrate 收尾只剩 `merge-pull-request`。

```yaml
- stage: build
  tasks:
    - id: build:open-pr        # mohist/create-pull-request
    - id: load-tasks
    - id: build:update-pr      # mohist/create-pull-request

- stage: check
  tasks:
    - id: ai-review
    - id: check:update-pr      # mohist/create-pull-request

- stage: integrate
  tasks:
    - integrate:spec-sync       # mohist/acp-agent
    - integrate:archive-change  # mohist/archive-change
    - integrate:merge-pr        # mohist/merge-pull-request
```

语义：

- plan 批准后第一个 task 是 `build:open-pr`，它在 build 一开始就
  force-with-lease 推送同一个 `workspace.branch`，并按 head/base 创建或
  复用 open PR；PR 身份通过 `setVars` 投影到
  `vars.github.pr.number` / `vars.github.pr.url`。
- `build:update-pr` 显式声明在 build stage 的尾部，等 build 的
  load-tasks 跑完后再推送一次 head/base 同样的 PR，更新 title/body。
- `check:update-pr` 显式声明在 check stage 的尾部，等 AI review 及其
  auto-fix 路径收敛后再把最终 review 结果推到同一个 PR。
- 正常路径不预先 rebase；integrate 的 happy path 只有 `integrate:merge-pr`。
- GitHub PR mergeability 是集成裁判；只有 GitHub 返回不可合并、base 已移动、
  branch out-of-date 等失败时，才进入 recovery rebase。
- `integrate:merge-pr` 读取 `vars.github.pr.number`，先等待 GitHub PR
  checks，再由 `gh pr merge --squash` 合并；merge 成功后确认 PR
  `state=MERGED` 才视为集成完成。
- merge 语义是把当前 workflow branch 合入 base branch；不要求 expected
  head SHA 或 `--match-head-commit`。
- PR checks pending 时，`integrate:merge-pr` 继续等待，直到 checks
  passed/skipped 或 failed。
- PR checks failed/cancelled/action_required 时，`integrate:merge-pr` 以
  `errorCode: pr-checks-failed` 失败；当前不自动修复，保留普通 task
  failure，由用户介入后 retry/rerun。

约束：

- 不新增 stage hook。
- 不新增隐藏的 stage boundary side effect。
- 不新增 workflow completion/finalize task。
- PR checks 不建模为 stage-level check；它们只是 `merge-pull-request`
  action 的内部前置条件。

前置条件：

- runner 主机安装 `gh` CLI。
- runner 主机完成 `gh auth login`。
- 仓库允许当前账号创建并 squash merge PR。

适用：

- 用户可见 feature。
- 需要 GitHub audit trail 的改动。
- 希望保留 AI 中间 commit 历史，同时 base branch 只留下 squash merge commit。

非目标：

- 不把 GitHub PR 作为人工审批门。
- 不接入 GitHub Actions / CI。
- 不同步 GitHub issue。
- 不由 Mohist 删除远端 head branch；依赖 GitHub repository setting。

## PR Recovery

`mohist/pr` 的恢复应基于 action output 编排，见 `actions.md`。

典型恢复：

- `output.errorCode: base-moved` → 插入 `mohist/rebase`，再插入 `mohist/create-pull-request` 和 `mohist/merge-pull-request`。
- `output.errorCode: config-error` → 阻塞 human 修 runner 环境。
- `output.errorCode: protection-conflict` → 阻塞 human 调整 GitHub 配置或 workflow。
- `output.errorCode: pr-state-conflict` → 阻塞 human；系统不擅自新开替代 PR。

恢复 task 使用同一个 workflow workspace。`recover:open-pr` 继续推送同一个 `workspace.branch`，因此 GitHub 会更新并复用同一个 open PR；如果 mergeability 仍失败，则保留普通 task failure，等待人工处理或下一次 retry。

## Selection

默认仍是 `mohist/default`。

选择 `mohist/pr` 的方式：

```bash
mo issue create "..." --workflow-profile mohist/pr
```

或在 issue frontmatter 里声明：

```yaml
---
recommended_workflow: mohist/pr
recommended_workflow_reason: This issue should be integrated through a traceable GitHub PR.
risk: medium
---
```
