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

`errorCode` 是推荐字段名，不是平台枚举。每个 action 自己定义可用 code。profile 作者引用的是该 action 的 output 接口。

非 JSON object output 在字段路径匹配里等价于无字段。

成功 task 的 JSON object output 应自动成为 task-local output。后续 task 可以通过 task id 读取，不需要 workflow profile 重新声明 action 的 output 字段：

```yaml
- id: integrate:open-pr
  uses: mohist/create-pull-request

- id: integrate:merge-pr
  uses: mohist/merge-pull-request
  with:
    prNumber: ${{ tasks.integrate:open-pr.outputs.prNumber }}
```

`tasks.<taskId>.outputs.*` 表示某个 task 的 action output。它适合直接 task-to-task wiring。

## Workflow Variable Projection

Action output 和 workflow variables 是两种机制。Action output 是 action 自己的接口；workflow variables 来自 workflow profile 的分层合并。

task 可以用 `setVars` 把 action output 的部分字段写入 workflow runtime profile：

```yaml
- id: integrate:open-pr
  title: Open or update GitHub PR
  uses: mohist/create-pull-request
  with:
    source: ${{ workspace.branch }}
    target: ${{ repository.baseBranch }}
    remote: origin
    title: "Complete issue #${{ issue.number }}"
  setVars:
    github.pr.number: output.prNumber
    github.pr.url: output.prUrl
    github.pr.headSha: output.headSha
```

`setVars` 的左侧是 runtime profile `vars` 下的目标路径，右侧是当前 action output 的 JSON path。上例 patch：

```yaml
vars.github.pr.number
vars.github.pr.url
vars.github.pr.headSha
```

后续 task 不直接读取 runtime profile，而是读取 profile layers merge 后的 effective `vars`：

```yaml
- id: integrate:merge-pr
  uses: mohist/merge-pull-request
  with:
    prNumber: ${{ vars.github.pr.number }}
    expectedHeadSha: ${{ vars.github.pr.headSha }}
```

规则：

- `setVars` 由 runner 在 report task 完成前执行，是 task 完成的一部分。
- `setVars` 只 patch workflow runtime profile 的 `vars.*`。
- `setVars` 不能覆盖 `workflow`、`stage`、`work`、`issue`、`workspace` 等 dispatch context。
- 恢复 task 可以重新写同一组 `vars.*`，覆盖旧运行态事实。
- 失败恢复匹配读取失败 task 的 raw action output，不依赖 `setVars`。

## Error Code

失败可恢复性属于 action output 接口，而不是 workflow domain 类型。

推荐：

- 用 `errorCode` 表达可编排错误码。
- 用 `message` 表达人读说明。
- 额外上下文字段由 action 自己定义，例如 `prNumber`、`prUrl`、`mergeCommitSha`。

避免：

- 不创建全局 `FailureKind`。
- 不要求所有 action 都返回同一组错误码。
- 不让 engine 理解 `base-moved`、`config-error` 等具体含义。

## Failure Recovery

task 可以声明失败恢复规则。规则只读取当前失败 task 的 output，不判断 action type，不判断 task id。

目标语义：

```yaml
- id: integrate:merge-pr
  title: Merge GitHub PR
  uses: mohist/merge-pull-request
  with:
    prNumber: ${{ vars.github.pr.number }}
    method: squash
    expectedHeadSha: ${{ vars.github.pr.headSha }}
  onFailure:
    limit: 2
    cases:
      - when:
          output.errorCode: base-moved
        tasks:
          - id: recover:rebase
            title: Rebase after base moved
            uses: mohist/rebase
            with:
              baseBranch: ${{ repository.baseBranch }}
              remote: origin
              squash: false
              conflictResolver:
                title: Resolve rebase conflicts
                with:
                  description: Resolve rebase conflicts, stage resolved files, and continue the rebase.
          - id: recover:open-pr
            title: Update GitHub PR
            uses: mohist/create-pull-request
            with:
              source: ${{ workspace.branch }}
              target: ${{ repository.baseBranch }}
              remote: origin
              title: "Complete issue #${{ issue.number }}"
            setVars:
              github.pr.number: output.prNumber
              github.pr.url: output.prUrl
              github.pr.headSha: output.headSha
          - id: recover:merge-pr
            title: Merge GitHub PR
            uses: mohist/merge-pull-request
            with:
              prNumber: ${{ vars.github.pr.number }}
              method: squash
              expectedHeadSha: ${{ vars.github.pr.headSha }}
```

执行规则：

- task failed 后，engine 解析该 task 的 output。
- 按 `cases` 顺序做动态字段匹配。
- 命中 case 时，把 case.tasks 插入当前 stage，workflow 回到 running。
- 超过 `limit` 或无 case 命中时，保留普通 task failure，用户仍可 retry/rerun。

边界：

- `onFailure` 是 workflow profile 对 action output 接口的编排，不是 action 内部重试。
- 恢复 task 与普通 task 一样由 runner 执行。
- rebase、push、PR merge 等副作用仍在 runner action 内执行。
- Workflow domain 只保存 output 并做通用 JSON path 匹配，不理解 error code 的业务含义。
