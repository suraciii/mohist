---
purpose: "Action 设计：action 接口、input/output schema、失败恢复编排边界。"
style: ["极简，只给目标态。"]
---

# Action Design

Action 是 workflow task 的执行接口。Workflow profile 通过 `uses` 选择 action，通过 `with` 传入 input；runner 执行 action 后上报 result 和 output。

## 边界

- action 定义自己的 input。
- action 定义自己的 output。
- Workflow engine 不维护统一 action output schema。
- Workflow engine 不定义全局 `FailureKind` / `ErrorKind` 枚举。
- Workflow domain 不吸收具体 action 的 output 字段语义。

Workflow engine 只负责：

- 展开 `tasks[*].with` 模板并传给 runner。
- 保存 task output。
- 按 task 声明把 action output 投影到 workflow variables。
- 在需要编排时，对 output 做通用 JSON path 匹配。
- 根据匹配结果插入 workflow profile 声明的恢复 task。

## Input

`tasks[*].with` 是 action input，由 action 自己定义。

```yaml
- id: integrate:rebase
  uses: mohist/rebase
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
    squash: false
```

Workflow 只负责模板展开，不解释 `baseBranch`、`remote`、`squash` 的业务语义。

## Action Output

`task.output` 是 action output，由 action 自己定义。

Action output 是完整的 JSON object（对标 `WithInput`）。`TaskRun.Output` 类型为 `JsonElement?`，直接存储 action 产出的解析后 JSON，不需要 `TaskOutputDefinition` / `capturedOutputs` 提取管道。

Action 可以返回文本 output，也可以返回 JSON object output。需要被 workflow profile 编排读取的 output 应返回 JSON object。

```json
{
  "errorCode": "base-moved",
  "message": "Pull request is not mergeable because the base branch moved",
  "prNumber": 42,
  "prUrl": "https://github.com/acme/repo/pull/42"
}
```

`errorCode` 这类字段是 action 自己定义的接口，不是平台枚举。profile 作者引用的是该 action 的 output 接口。

非 JSON object output 在字段路径匹配里等价于无字段。

成功 task 的 JSON object output 应自动成为 task-local output。后续 task 可以通过 task id 读取，不需要 workflow profile 重新声明 action 的 output 字段：

```yaml
- id: discover-change
  uses: example/discover-change

- id: consume-change
  uses: example/consume-change
  with:
    changeId: ${{ tasks.discover-change.outputs.changeId }}
```

`tasks.<taskId>.outputs.*` 表示某个 task 的 action output。它适合直接 task-to-task wiring。

## Workflow Variable Projection

Action output 和 workflow variables 是两种机制。Action output 是 action 自己的接口；workflow variables 来自 workflow profile 的分层合并。

task 可以用 `setVars` 把 action output 的部分字段写入 workflow runtime profile：

```yaml
- id: discover-change
  title: Discover change metadata
  uses: example/discover-change
  with:
    path: ${{ openspecChangeDir }}
  setVars:
    change.id: output.changeId
    change.url: output.changeUrl
```

`setVars` 的左侧是 runtime profile `vars` 下的目标路径，右侧是当前 action output 的 JSON path。上例 patch：

```yaml
vars.change.id
vars.change.url
```

后续 task 不直接读取 runtime profile，而是读取 profile layers merge 后的 effective `vars`：

```yaml
- id: consume-change
  uses: example/consume-change
  with:
    changeId: ${{ vars.change.id }}
```

规则：

- `setVars` 由 runner 在 report task 完成前执行，是 task 完成的一部分。
- `setVars` 只 patch workflow runtime profile 的 `vars.*`。
- `setVars` 不能覆盖 `workflow`、`stage`、`work`、`issue`、`workspace` 等 dispatch context。
- 恢复 task 可以重新写同一组 `vars.*`，覆盖旧运行态事实。
- 失败恢复匹配读取失败 task 的 raw action output，不依赖 `setVars`。

## Artifacts

`artifacts` 是 task 产物的捕获清单，不是硬性契约。语义是"存在则上传，不存在则跳过"。

- artifact 路径不存在 → 跳过，记 debug 日志。
- artifact 目录为空 → 跳过，记 debug 日志。
- artifact 文件存在 → 正常捕获上传。
- artifact 捕获失败**不让 task 失败**。

`expect` 是 task 的完成契约。只有 `expect` 断言失败才让 task 失败。

职责分离：

- `expect.files`：断言路径必须存在（task 级硬约束）。
- `artifacts.files`：声明要捕获哪些产物（尽力捕获，非约束）。

workflow YAML 里：硬性要求的路径同时放 `expect` + `artifacts`；条件性的路径只放 `artifacts`。

```yaml
# proposal.md — 必须有
expect:
  files:
    - path: ${{ openspecChangeDir }}/proposal.md
artifacts:
  files:
    - path: ${{ openspecChangeDir }}/proposal.md

# specs/ — 可能有，也可能没有（纯性能/重构 issue 无 spec 变更）
artifacts:
  files:
    - path: ${{ openspecChangeDir }}/specs
```

## Error Code

失败可恢复性属于 action output 接口，而不是 workflow domain 类型。

推荐：

- 用 `errorCode` 表达可编排错误码。
- 用 `message` 表达人读说明。
- 额外上下文字段由 action 自己定义，例如 `prNumber`、`prUrl`、`mergeCommitSha`。

避免：

- 不创建全局 `FailureKind`。
- 不要求所有 action 都返回同一组错误码或字段名。
- 不让 engine 理解 `base-moved`、`config-error` 等具体含义。

Recovery handler 的 `when` 表达式匹配 action output 的任意字段（如 `errorCode=base-moved`、`promise=FAIL`、`failureKind=conflict`），不限定字段名。见 [`recovery.md`](recovery.md)。

## GitHub PR Actions

`mohist/create-github-pr`、`mohist/mark-github-pr-ready`、`mohist/push`
和 `mohist/merge-github-pr` 都是普通 workflow task action。PR 相关副作用
必须显式出现在 task graph 里，不通过 stage hook 或隐藏的 stage boundary
side effect 执行。

边界：

- `create-github-pr` 负责推送 workflow branch，并创建或更新同 head/base
  的 draft PR。
- `create-github-pr` 输出稳定 PR 身份，profile 可用 `setVars` 投影
  `vars.github.pr.number` / `vars.github.pr.url`。
- `mark-github-pr-ready` 只负责把 `prNumber` 指向的 draft PR 标记为 ready。
  它不推送 branch，不更新 title/body，PR 已经 ready 时幂等成功。
- `push` 负责把本地 workflow branch 显式同步到远程 PR head；rebase 后可
  使用 `forceWithLease` 更新同一条线性分支。
- `merge-github-pr` 负责把 PR 合入 base branch。
- `merge-github-pr` 在真正 merge 前必须先等待 GitHub PR checks。
- PR checks 是 merge action 的内部前置条件，不建模成 stage-level check。
- profile 只为预期中的 mergeability 错误声明 recovery。配置、认证、PR 状态
  冲突和 GitHub API 异常保留普通 task failure。

目标 task graph 见 [`builtin-workflows/github-pr.md`](builtin-workflows/github-pr.md)。

`merge-github-pr` 流程：

```text
resolve PR
  -> inspect PR state
  -> wait PR checks
       pending: keep waiting
       passed/skipped: continue
       failed/cancelled/action_required: fail task
  -> gh pr merge --squash
  -> confirm state=MERGED
```

PR checks 等待阶段轮询 `gh pr view <prNumber> --json statusCheckRollup`：

- `status != COMPLETED`：继续等待。
- 全部 `conclusion == SUCCESS / NEUTRAL / SKIPPED`：继续 merge。
- `FAILURE` / `CANCELLED` / `ACTION_REQUIRED` / 其它失败 conclusion：返回 `errorCode: pr-checks-failed`。
- `statusCheckRollup` 为空：分支此刻零 check run。常发生在刚 push / force push 后、GitHub 尚未把 workflow run 注册成 check 的窗口；也覆盖无 CI 的仓库。按 `PENDING` 同样继续轮询，超过 grace 窗口（默认 120s）仍零 check 才继续 merge——避免把"check 还没注册"误判成"check 失败"。
- `gh` 其它非零退出：返回 `errorCode: pr-checks-failed`。
- `context.signal` 触发：返回 `retry-safe`。

PR checks 失败时，`merge-github-pr` 返回 action-owned JSON failure，例如：

```json
{
  "kind": "merge-github-pr",
  "status": "failed",
  "errorCode": "pr-checks-failed",
  "message": "PR #42 checks failed: build failed",
  "prNumber": 42,
  "prUrl": "https://github.com/acme/repo/pull/42"
}
```

`pr-checks-failed` 不触发隐式 auto-fix。需要自动修复时，profile 必须对
`output.errorCode: pr-checks-failed` 声明显式 recovery task。

## Failure Recovery

Task 遇到可恢复失败时，由 runner executor（不是 action 自身）用 `when` 表达式
匹配 action output，决定需要哪些 recovery tasks，通过 result 返回给 engine 插入。
Recovery 是 task 完成的一部分，不是失败后的补救。

详细设计见 [`recovery.md`](recovery.md)。

核心边界：

- recovery 的决策和 task 构造在 runner executor 侧，不在 action 内部，不在 workflow engine。
- `recovery` 是 task 顶级属性（与 `with`、`artifacts` 并列）。runner executor 读取并递减 budget。
- engine 收到带 `addTasks` 的 completed result 时，机械插入这些真实
  workflow tasks，不理解其内容。
- rebase、push、PR checks wait、PR merge 等副作用仍在 runner action 内执行。
- `titleFrom`、`bodyFrom`、`subjectFrom`、`messageFrom` 这类 `issue.title` / `issue.body` 输入不是 workflow metadata。runner action 在运行时通过 `mo issue show <number> --project-id <projectId> --output json` 获取 issue title/body。
- Workflow domain 只保存 output，不理解 error code 的业务含义。
