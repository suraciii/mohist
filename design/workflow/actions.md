# Action 设计

Action 是 Workflow task 的执行接口。`uses` 选择 Action，`with` 传入输入，Runner 执行后
报告结果与输出。

## 边界

- Action 定义自己的输入与输出。
- Engine 不维护统一的 Action output schema。
- Engine 不定义全局 `FailureKind` / `ErrorKind`。
- Engine 不解释 Action output 的业务语义。

Engine 只负责：展开 task input 和由 Workflow 拥有的完成声明，保存 task output，通过
`setVars` 把 output 投影到 Run Variables，按 `when` 匹配 recovery，
以及机械地插入 recovery task。

## 输入

```yaml
- id: integrate:rebase
  uses: mohist/rebase
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
```

Workflow 负责展开模板，不解释 `baseBranch`、`remote` 的业务含义。

## 输出

`TaskRun.Output` = `JsonElement?`，完整保存 Action 返回的 JSON output。

```json
{
  "errorCode": "base-moved",
  "message": "PR not mergeable",
  "prNumber": 42
}
```

`errorCode` 等字段属于该 Action 的接口，不是平台 enum。

下游 task 可以通过 `${{ tasks.<id>.outputs.* }}` 读取 task output。

## setVars

把 Action output 字段投影到 Run Variables：

```yaml
setVars:
  change.id: output.changeId
  change.url: output.changeUrl
```

- 左侧是 `vars` 下的 path，右侧是 Action output 中的 JSON path。
- Runner 在报告 task complete 前执行 `setVars`；投影失败则 task 失败。
- 只能修改 `vars.*`，不能修改 `workflow`、`stage`、`work`、`issue`、`workspace`。
- Recovery task 可以覆盖相同的 `vars.*`。
- Runner 使用与其他调用方相同的 Run Variables PATCH API，但生成的 body 只包含
  `vars`，不包含 `stages`。完整语义见 [`variables.md`](variables.md)。

## `artifacts`

声明需要采集的输出。采集是 best-effort：文件不存在时跳过，不让 task 失败。

```yaml
artifacts:
  files:
    - path: docs/proposal.md
```

## expect

`expect` 是由 Workflow 拥有的 task 完成契约，与 Action Input 分离。作者可见语义
（失败规则、与 `artifacts` 的搭配）见
[docs 的 expect 节](../../docs/workflow-definition.md#expect--完成要求)。Runner 的
Workflow task executor 同时接收展开后的 Action Input 和 `expect`，在 Action 执行后
应用完成判断；Action 与 Runtime 模块都不解释它。

marker 的 `path` 可以是特殊值 `_output`，表示对回合最终 assistant 文本匹配，而不是
文件内容。task executor 从 Action result 携带的回合事实中取得该文本；它不进入
Action Output，也不要求 Action 额外声明。

`_output` 只识别 promise-tag 形式（`<promise>VALUE</promise>`）。多个被接受的值出现
时，按文本中最后出现的为准（与 file marker 的“声明顺序优先”不同）。若需要按字面
substring 匹配最终 assistant 文本，请把字面值编码为 `oneOf` 中 promise tag 的内部
VALUE。`_output` 不读取文件系统，evidence 也不会把它当作可抓取的文件路径。

## 错误字段与 recovery

Action output 中的错误字段，例如 `errorCode`、`promise`，都属于 Action 自己的契约。

Recovery `when` 可以匹配任意字段，例如 `errorCode=base-moved`、`promise=FAIL`、
`errorCode=conflict`。

系统没有全局 error enum，Engine 也不理解具体错误含义。Recovery 设计见
[`recovery.md`](recovery.md)。

## OpenCode Action

`mohist/opencode` 是 Runtime 特有的 Action；它与 Agent / Session 的所有权关系（直接
使用即 Inline Agent、不解析 Agent 定义等不变量）见
[`../agent-execution.md`](../agent-execution.md)。Runtime 已由 `uses` 选择，因此输入
不需要 `kind` 或 `type` discriminator。

输入契约：

```ts
type OpenCodeActionInput = {
  prompt: string                    // 已展开的非空字符串
  session?: string                  // 逻辑 Session 名称
  options?: {
    model?: string                  // provider/model；model 自身可包含 '/'
    variant?: string                // 与 model 同级的独立字段，不拼进 model ID
  }
}
```

Action 只接收展开后的 `prompt`、可选逻辑 `session` 名称和可选 OpenCode 模型
`options`（`model` / `variant`）。`options` 通常由 `${{ vars.agent }}` 整值展开而来，
模板展开语义与示例见 [`profile.md`](profile.md)。`options` 中除 `model` 与 `variant`
之外的键被忽略并记入诊断，不使回合失败。Workflow 把 `expect` 作为 task 完成契约
单独提供；Action 不会把 `vars.agent` 当作隐藏 fallback，也不再读取 `with` 内部的
`expect` / `agent`。旧结构 `with.expect`、`with.agent` 在 profile 加载阶段就被可操作
错误拒绝。

输出契约：

```ts
type OpenCodeActionOutput = null | { promise: string }
```

除非 expectation 命中 promise marker，否则 output 为 `null`；命中时只返回：

```json
{ "promise": "PASS" }
```

该 `{ promise }` output 由 Workflow task executor 依据 `expect` 合成；Action 与
Runtime 都不产生它。Runtime Session 身份、model、usage、transcript、诊断信息和
expectation 明细保存在各自所属模型中，不复制到 Action output。OpenCode 实现见
[`../runtimes/opencode.md`](../runtimes/opencode.md)。

## GitHub PR Action

`mohist/create-github-pr`、`mark-github-pr-ready`、`push`、`merge-github-pr` 都是普通
Workflow Action。

- `create-github-pr`：推送 Workflow branch，创建或更新 draft PR，输出稳定 PR 身份。
- `mark-github-pr-ready`：把 draft PR 标记为 ready；已经 ready 时保持幂等。
- `push`：把本地 branch 同步到远端 PR head，可以使用 `forceWithLease`。
- `merge-github-pr`：以 squash 方式合并 PR；执行 merge 前必须等待 PR checks。

等待 PR checks 是 merge Action 的内部前置条件，不是 stage-level check。它轮询
`gh pr view --json statusCheckRollup`。checks 为空时在 120 秒 grace window 内等待；
checks 失败时返回 `errorCode: pr-checks-failed`。Action 不做隐式自动修复，profile 必须
声明显式 recovery。

完整 task graph 见 [`builtin-workflows.md`](builtin-workflows.md)。
