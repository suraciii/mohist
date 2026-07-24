# Action 设计

Action 是 Workflow task 的可插拔执行单元:`uses` 选择 Action,`with` 传入全部输入,
返回结构化 output 或 error。开发者写一个 Action = 写一份声明式契约(manifest)+
一个纯函数实现,体验对齐 GitHub Actions。

Action 不拥有 Workflow 的完成判断(`expect`),不代表有身份的 Mohist Agent,也不是
独立进程——它是 Runner 进程内注册的受信任模块。

## Model

### Manifest

每个 Action 由一份 manifest 声明契约。manifest 是纯数据(可序列化为 JSON),与实现
同文件定义,通过 `defineAction` 获得类型推导:

```ts
export const rebaseAction = defineAction({
  name: "mohist/rebase",
  description: "把 workflow branch rebase 到 base branch 上",
  inputs: {
    baseBranch: { type: "string", required: true },
    remote: { type: "string", default: "origin" },
  },
  outputs: {
    headSha: { type: "string", description: "rebase 后的 HEAD" },
  },
  errors: {
    "rebase-conflict": "rebase 产生冲突,需要人工或恢复任务处理",
  },
  run: async (inputs, host) => {
    // inputs: { baseBranch: string; remote: string } —— 已校验、已填默认值
    const result = await host.exec("git", ["rebase", `${inputs.remote}/${inputs.baseBranch}`])
    if (!result.ok) return err("rebase-conflict", result.stderr)
    return ok({ headSha: result.stdout.trim() })
  },
})
```

- `name` 是 `uses` 匹配键,小写,`<namespace>/<action>` 形式,无版本段。
- `inputs` 每项声明 `type`(`string | number | boolean | object | array`)、
  `required` 或 `default`(二者互斥)、`description`。
- `outputs` 声明成功 output 的字段,是文档与投影契约(`setVars`、
  `tasks.<id>.outputs.*` 的可用路径来源)。
- `errors` 声明该 Action 全部业务 error code(kebab-case)及含义,供 recovery
  `when: error.code=...` 匹配与文档生成。

平台保留两类不进 manifest 的标识:保留输入 `working-directory`(engine 消费,决定
`host.workDir`,Action 不可声明同名输入)和平台 error code(`invalid-input`、
`unexpected-error`、`timeout`,由 engine 产生,Action 不得自造)。

### 实现接口与 host

`run(inputs, host)` 是 Action 的全部实现面。默认 host 只有:

```ts
interface ActionHost {
  workDir: string                 // 已解析的执行目录
  signal: AbortSignal
  log(source: string, line: string): void
  exec(cmd: string, args: string[], options?): Promise<ExecResult>
}
```

Action 不接触 Run Variables、server 连接、runtime 句柄、recovery 声明或 dispatch
元数据。需要上下文数据的,一律由 profile 通过 `with` 模板显式传入。

### 能力(capabilities)

超出默认 host 的能力必须在 manifest 声明,engine 按声明注入,不声明则不可见:

| 能力 | 注入 | 用途 |
| --- | --- | --- |
| `agent-execution` | `host.agent.execute({ prompt, session?, options? })` | 执行一次 Agent 输入。Session 打开/attach、Runtime 生命周期由能力实现层处理,Action 只表达意图 |
| `add-tasks` | 允许结果携带 `addTasks` | 追加后续 task,由 engine 统一上报,Action 不直连 server |
| `write-vars` | `host.writeVars(vars)` | 执行中即时持久化 `vars.*`(与完成后投影的 `setVars` 不同,失败不回滚,供重试观察) |

声明 `agent-execution` 同时意味着:该 Action 的执行会产生 Runner-private execution fact
(最终 assistant 文本),由能力实现层记录,供 `expect` 的 `_output` marker 匹配;
Action 结果本身不携带它。

### Registry 与 catalog

Runner 内置 Action 在一处列表注册,registry 由 manifest 构建,`uses` 大小写不敏感
匹配 `name`。全部 manifest 汇总为 catalog(纯 JSON),连同退役 Action 的 tombstone
(name + 指引文案)在 Runner 注册时上报 server。

外部插件加载、`uses` 版本段(`@v1`)、组合 Action(YAML 编排 steps)都是非目标;
扩展点保留为 registry 接受额外的 `defineAction` 集合。

## Semantics

### 输入

输入单通道:Action 的全部输入来自渲染并校验后的 `with`,Runner 在调用 Action 前完成
渲染和 manifest 校验,Action 不接触 raw `with`、Variables resource 或 dispatch context。
渲染时机与 attempt 快照语义以 [`task-dispatch.md`](task-dispatch.md) 为权威。

```yaml
- id: integrate:rebase
  uses: mohist/rebase
  with:
    baseBranch: ${{ repository.baseBranch }}
    remote: origin
```

Runner 执行入口的处理顺序与失败行为(每一步失败都使 Action 不被调用):

1. 在 attempt 快照上克隆原始 `with`,识别 manifest 声明 `render: deferred` 的字段并保留
   原值;其余字段递归展开 `${{ ... }}`(未解析引用按 dispatch 契约失败,见
   [`task-dispatch.md`](task-dispatch.md))。
2. `expect` 同样在 attempt 快照上渲染;渲染结果只作为 Workflow 拥有的完成契约,不进入
   Action 输入通道。
3. 按 manifest 校验渲染后的 inputs:未知输入键 → `invalid-input` 失败,不静默忽略。
4. 缺失 `required` 输入 → `invalid-input` 失败。
5. 类型不匹配 → `invalid-input` 失败。
6. 应用 `default`,交给 `run` 的是完整、强类型的 inputs。

manifest 声明 `render: deferred` 的字段不参与第 1 步递归展开,原样进入第 3–6 步校验,
保留内部 `${{ ... }}` 供确实要生成后续 task 的 Action 传播。其余对象/数组字段按普通规则
递归展开。

输入之间的一致性约束(例如 merge 前置条件)属于 Action 语义,写在 `run` 开头,
失败返回 manifest 声明的 error code 或 `invalid-input`。

### 结果

Action 的公开结果是二选一:

```json
{ "output": { "prNumber": 42, "prUrl": "https://github.com/example/repo/pull/42" } }
```

```json
{ "error": { "code": "pr-checks-failed", "message": "PR #42 checks failed. Fix the failures and retry." } }
```

- `output` 是 JSON object 或 `null`,端到端保持结构化:Runner 内部、上报 wire、
  `TaskRun.Output` 存储、`setVars` 投影、`tasks.<id>.outputs.*` 读取、recovery
  `when: output.*` 匹配都作用于同一个 object,任何环节不做字符串化再解析。
- `error.code` 必须是 manifest `errors` 声明的 code 或平台 code。
- `error.message` 是唯一用户可见的错误文案;error 不携带额外 details,原始命令
  输出和诊断进入 task log。
- 原生异常不是协议的一部分;engine 在 Action 边界把它规范化为 `unexpected-error`。
- 声明 `add-tasks` 能力的 Action 可在成功结果中附带 `addTasks`,由 engine 上报。
- engine 不对成功 output 做运行期 schema 校验;声明了 `outputs` 但缺字段的问题在
  `setVars` 投影处以明确错误暴露。

`TaskRun.Output` 只保存 Action 成功 output。Action 成功后被 `expect`、工作区约束
或其他 Runner 后置检查判定失败时,TaskRun 可以同时保存原 output 与 Runner 产生的
error。Task status、exit code 和 Runner-private execution fact 属于 Task 执行协议,不属于
Action 公开结果。

engine 对结果的通用处理不解释任何 Action 的业务语义。唯一按能力分派的行为:声明
`agent-execution` 的 Action,其 output 由 task executor 依据 `expect` 投影为
`null | { promise }`(见下文 expect 节);其余 Action 的 output 原样保留。不存在
按 `uses` 名单的特判。

### 校验时机与 catalog 消费

- **Profile 保存/更新时**:server 用最近上报的 catalog 做全量校验——未知 `uses`、
  未知输入键、缺 `required`、常量输入的类型错,都是可操作错误。含模板表达式的输入
  只校验键名,类型留到 Runner 执行入口。catalog 尚未上报时跳过此层并记录,不阻塞保存。
- **Runner 执行入口(权威,fail-closed)**:Runner 在 attempt 快照上渲染原始 `with` 后,
  按本地 manifest 强制校验,失败即 task 失败(`invalid-input`),不会以未校验输入调用
  `run`。Server 不再做 dispatch 前展开。
- **退役 Action**:Runner 渲染时命中 tombstone → 以 tombstone 指引文案失败;profile
  保存命中 → 拒绝保存。

Profile 保存先由 Workflow Definition 校验器产生语义模型，再使用 catalog 判断 `uses` 与
`with`。Definition 校验器只递归检查 `with` 值中的模板表达式；catalog 不重复判断
Definition 字段或模板命名空间。两类诊断合并进同一条校验异常，使用同一 YAML path 规则
并以来源标签区分。保存成功响应显式携带 `actionValidation: { performed, reason? }`，
告知调用方 Action-contract 校验是否执行；catalog 不可用时按上文跳过并在响应中说明。
内置 Profile 加载、运行时加载与 `mo run validate` 只做 Definition 校验，不依赖
catalog。legacy `with.agent` / `with.kind` / `with.type` / `with.expect` 不作为特例
存在，一律按未知输入键拒绝。

### setVars

把 Action output 字段投影到 Run Variables:

```yaml
setVars:
  change.id: output.changeId
  change.url: output.changeUrl
```

- 左侧是 `vars` 下的 path,右侧是 Action output 中的 JSON path。
- Runner 在报告 task complete 前执行 `setVars`;投影失败(含 path 不存在)则 task
  失败,不静默跳过。
- 只能修改 `vars.*`,不能修改 `workflow`、`stage`、`work`、`issue`、`workspace`。
- Recovery task 可以覆盖相同的 `vars.*`。
- Runner 使用与其他调用方相同的 Run Variables PATCH API,但生成的 body 只包含
  `vars`,不包含 `stages`。完整语义见 [`variables.md`](variables.md)。

### artifacts

声明需要采集的输出。采集是 best-effort:文件不存在时跳过,不让 task 失败。

```yaml
artifacts:
  files:
    - path: docs/proposal.md
```

### expect

`expect` 是由 Workflow 拥有的 task 完成契约,与 Action 输入分离。作者可见语义
(失败规则、与 `artifacts` 的搭配)见
[docs 的 expect 节](../../docs/workflow-definition.md#expect--完成要求)。Runner 的
task executor 在 attempt 快照上渲染 `expect`,只在 Action 成功后应用完成判断;Action
失败、取消或超时时直接保留原始失败,不读取文件或 marker。Action 与能力实现层都不
解释它,渲染后的 `expect` 也不进入 Action 输入通道。

marker 的 `path` 可以是特殊值 `_output`,表示对本次执行最终 assistant 文本匹配,而不是
文件内容。task executor 从 `agent-execution` 能力记录的 execution fact 中取得该文本;它不进入
Action output,也不要求 Action 额外声明。

`_output` 只识别 promise-tag 形式(`<promise>VALUE</promise>`)。多个被接受的值出现
时,按文本中最后出现的为准(与 file marker 的"声明顺序优先"不同)。若需要按字面
substring 匹配最终 assistant 文本,请把字面值编码为 `oneOf` 中 promise tag 的内部
VALUE。`_output` 不读取文件系统,evidence 也不会把它当作可抓取的文件路径。

### Error 与 recovery

Runner 为 recovery 构造 `{ output, error }` 上下文。显式 `when` 使用该上下文的 path:

```yaml
handlers:
  - when: error.code=rebase-conflict
  - when: output.promise=FAIL
```

没有 `when` 的最后一个 handler 是存在 error 的兜底。它可以处理 executor 在 Action
完成后发现的失败,例如工作区不干净。`error.message` 不是机器协议,禁止用于 `when`。

系统没有全局 Action error enum,engine 也不理解具体错误含义;error code 的权威目录
是各 Action 的 manifest。Recovery 设计见 [`recovery.md`](recovery.md)。

### checks

Stage check 项通过相同的 `uses`/`with` 复用同一 Action 契约。成功/失败到
pass/fail 的映射由 check 宿主完成,Action 不感知自己运行在 task 还是 check 中。

## 内置 Action

### `mohist/opencode`

Runtime 特有的 `agent-execution` Action;它与 Agent / Session 的所有权关系(直接使用即
Inline Agent、不解析 Agent 定义等不变量)见 [`../agent-execution.md`](../agent-execution.md)。
Runtime 已由 `uses` 选择,输入不需要 `kind` 或 `type` discriminator。

输入契约:

```ts
type OpenCodeActionInput = {
  prompt: string                    // Runner 渲染后的非空字符串
  session?: string                  // 逻辑 Session 名称
  options?: {
    model?: string                  // provider/model;model 自身可包含 '/'
    variant?: string                // 与 model 同级的独立字段,不拼进 model ID
  }
}
```

`options` 通常由 `${{ vars.agent }}` 整值展开而来,模板求值时机以
[`task-dispatch.md`](task-dispatch.md) 为权威。`options` 中除 `model` 与 `variant`
之外的键被忽略并记入诊断,不使执行失败。Workflow 把 `expect` 作为 task 完成契约单独
提供;旧结构 `with.expect`、`with.agent` 在 profile 加载阶段就被可操作错误拒绝。

输出契约:

```ts
type OpenCodeActionOutput = null | { promise: string }
```

该 `{ promise }` output 由 task executor 依据 `expect` 对 `agent-execution` Action 合成;
Action 与能力实现层都不产生它。Runtime Session 身份、model、usage、transcript、
诊断信息和 expectation 明细保存在各自所属模型中,不复制到 Action output。OpenCode
实现见 [`../runtimes/opencode.md`](../runtimes/opencode.md)。

### Git 与 GitHub PR Action

`mohist/push`、`create-github-pr`、`mark-github-pr-ready`、`merge-github-pr`、
`mohist/rebase` 都是普通 Workflow Action,遵循输入单通道:base branch、workflow
branch、remote 等由 profile 通过 `${{ repository.* }}` / `${{ workspace.* }}` 显式
传入,Action 不反查 Run Variables。

- `push`:唯一负责把当前 workspace 的已提交 HEAD 发布到远端 workflow branch。workflow
  branch 由一个 WorkflowRun 独占,因此 Profile 使用强制更新,不依赖 remote-tracking ref。
- `create-github-pr`:只创建或更新 draft PR,输出稳定 PR 身份;它不执行 Git 操作,也不
  决定哪个提交应被发布。
- `mark-github-pr-ready`:把 draft PR 标记为 ready;已经 ready 时保持幂等。
- `merge-github-pr`:以 squash 方式合并 PR;执行 merge 前必须等待 PR checks。

发布、PR 元数据和 merge 是三个独立 task,因此失败边界也独立:push 失败只重试 push,
PR 操作失败只重试 PR,merge 恢复只处理 merge 自己的失败。

等待 PR checks 是 merge Action 的内部前置条件,不是 stage-level check。它轮询
`gh pr view --json statusCheckRollup`。checks 为空时在 120 秒 grace window 内等待;
checks 失败时返回 `error.code: pr-checks-failed`。Action 不做隐式自动修复,profile 必须
声明显式 recovery。

`mohist/github-pr-checks` 把同一套轮询/分类逻辑暴露成可在 stage graph 中声明的显式 task
（典型用法:check 阶段在 `mark-pr-ready` 之后做交付前 CI 门控）。它复用 merge Action 内部
的轮询纯函数与 `pr-checks-failed` error code,因此 profile 的 recovery handler 与 merge-pr
完全对称(同样的 `recover:fix-pr-checks` + `recover:push` + `retrySelf`)。它只读校验:
不改 PR、不 push、不做隐式修复,profile 声明显式 recovery。

完整 task graph 见 [`builtin-workflows.md`](builtin-workflows.md)。

## Status

正文是目标 spec;当前实现差距如下,待拆 issue 推进。

1. **无 manifest 与 defineAction**:registry 是硬编码 name→handler Map,输入靠各
   Action 手写 `stringInput` 解析,无未知键/类型/required 校验,无 catalog 上报,
   profile 保存时无法校验 `uses` 与输入。
2. **Action 输入边界**:交付类 Action 已通过 manifest 和显式 `with` 输入声明
   repository、branch、remote 与 PR identity;凭据仍是外部交付的授权边界。
3. **engine 内按名特判**:executor 的 `PROMISE_PROJECTED_ACTIONS` 与
   `REMOVED_ACTIONS` 名单,目标分别由 `agent-execution` 能力声明与 catalog tombstone
   取代。
4. **Action 越权访问**:`openspec-tasks` 直连 `serverConnection.addTasks`,
   `ActionContext` 全量暴露 server 连接与 runtime 句柄;目标收敛为默认 host + 声明式
   能力注入。
