# Task Dispatch

## Attempt input

`TaskRun` 和持久 `WorkItem` 保存 task declaration，而不是某次执行得到的值。`with` 与
task-level `expect` 中的 `${{ }}` 表达式在 task 的整个生命周期内保持原样。人工 retry、
recovery continuation 和 redelivery 都不能把某次 rendered input 写回 declaration。

每个 attempt 按以下顺序建立输入：

1. Server 读取当前 Effective Stage Variables、Project Prompts 和其它 Server-owned runtime
   context，形成该 attempt 的 context snapshot。
2. dispatch 同时携带未展开的 `with` / `expect` declaration 与 context snapshot。两者是不同
   数据，不能用一个 `with` 字段同时表示 declaration 和 rendered input。
3. Runner 解析本次 workspace，将 Runner-owned context 合入 snapshot，然后在调用 Action
   前把 declaration 展开为局部 rendered input。
4. Runner 按 Action manifest 校验 rendered input，再调用 Action。展开值只存在于本次
   Action 调用中；Runner 和引擎不得把它复制进 TaskRun、后续 `addTasks` 或 retry 来源。
   Action 明确返回的动态 task declaration 仍按 `add-tasks` 能力处理。

attempt 的 context snapshot 在 Runner 接受该 attempt 后固定。已经开始的 attempt 不因
Variables 或 Prompt 更新而改变；尚未派发的 task，以及 retry 或 recovery 创建的新 attempt，
重新取得当前 context。Runner 不在 Action 执行中再次向 Server 拉取更新后的 Variables。

## Task configuration expansion

`tasks[*].with` 和 task-level `expect` 可以包含 `${{ }}` 表达式。Runner 使用本次 attempt 的
context snapshot 统一展开：

```text
${{ path }} 占据整个值      -> 替换为对应值并保留 JSON 类型
其他可解析表达式           -> 替换为对应 dispatch context
${{ prompts.<key> }}       -> 替换为该 attempt 读取到的 Prompt body
字符串内嵌入的表达式       -> 值转文本拼入；解析不出值或值为对象/数组 -> 任务失败
任一完整表达式解析不出值   -> 任务失败
普通值                    -> 原样保留
```

嵌入拼接与 `\${{` 转义的作者可见语法以
[docs 的模板表达式节](../../docs/workflow-definition.md#模板表达式) 为权威。

Prompt body 不属于持久化的 task declaration。建立新 attempt 时按 key 读取当前 body，并与
其它 context 一起固定；完整解析与 fallback 规则见
[`../prompt-management.md`](../prompt-management.md)。

`${{ failure.* }}` 例外：Runner 构造 recovery handler task 时，只把绑定触发 attempt 的
`failure.*` 就地展开。插入引擎的 task declaration 不再含该表达式；其中的 `vars.*`、
`prompts.*` 和其它普通引用继续保持原样，在该恢复 task 自己的 attempt 开始时展开。

Variables 的解析、跨 scope merge 与动态生效语义见 [`variables.md`](variables.md)。
Runner 局部生成的 rendered `with` 是 Action 唯一的变量与配置输入；Action 不再次读取
Variables resource。`with` 内部不再做 same-key deep-merge，配置进入 Action 只能通过显式
`options: ${{ vars.agent }}` 等整值 `${{ vars.* }}` 绑定。

Action manifest 可以把确实需要传播模板的一个 input 声明为 `render: deferred`。Runner 对
该 input 保留内部表达式，但仍通过同一个 manifest 校验和 Action input 通道传入；Action
不会额外获得 raw task、Variables 或完整 context。其它 input 默认立即递归展开。

`expect` 不支持 deferred rendering。Runner 在 Action 调用前单独展开，并在 Action 返回后
作为 Workflow 拥有的 task 完成契约执行。它不进入 `with`，也不属于 Runtime-specific
Action Input。

Runner 为每个 WorkItem 只解析一次 workspace，并把结果作为 `ActionContext.workDir` 交给
Action。Action 不得从 `variables.workspace.path` 重新选择工作目录；后者只是 dispatch
context 的可见事实，不是第二个执行入口。

持久 WorkItem 的 `uses` / `with` 若违反所选 Action 的静态输入契约，或 attempt context
无法完成模板展开，Runner 必须返回确定的 `invalid-input` 失败。已经 claim 的 TaskRun 必须
以精确的 `workerId + workId` 报告失败；不得靠 poll redelivery 重试同一份确定无效的输入。

## Dispatch context

作者可见的命名空间清单以 [docs 的模板表达式表](../../docs/workflow-definition.md#模板表达式)
为权威；本表只补充实现侧来源：

| Variable | Source |
|---|---|
| `workflow.runId` | dispatch |
| `stage.name` | dispatch |
| `work.*` | dispatch；包括 `id`、`type`、`title`、`attempt` |
| `work.approvalFeedback.*` | 仅由 ApprovalFeedback 产生的 task；包括 `id`、`stage`、`createdAt`、`summary` |
| `issue.*` | Issue context；包括 `projectId`、`number`、`title`、`body` |
| `repository.*` | Issue 的目标仓库引用；dispatch 时从 Project Repository resource 解析 |
| `workspace.*` | Runner 执行时解析的 workspace |
| `vars.*` | Effective Stage Variables |
| `tasks.<id>.outputs.*` | previous task output |
| `prompts.<key>` | Project Prompt；Server 建立 attempt context 时按 key 读取当前 body |
| `failure.output` | 触发恢复的任务 output；runner 构造恢复任务时展开，仅恢复任务可用 |
| `failure.error.code` | 触发恢复的 error code；仅恢复任务可用 |
| `failure.error.message` | 触发恢复的可操作错误文案；仅恢复任务可用 |

Runtime context、Workflow Variables 与 Project Prompts 是三个独立命名空间。完整的
dispatch/report 流程见 [`../runner.md`](../runner.md)。

Effective Variables 只放在 `vars` 下，不把变量 key 复制成顶层裸名；Runtime context 也不
写回或合并进 Variables。`work.approvalFeedback` 只随由该反馈产生的 task 存在，普通 task
不携带。OpenSpec 目录不是 runtime context，由 Profile 与 Prompt 使用
`openspec/changes/issue-${{ issue.number }}` 明文表达。

### 子 Issue Plan 的父背景

API poll route 在把内部 `WorkDispatch` 映射为 HTTP 响应时，可以附加父 issue 当前标题与
body。只有 `workType = task`、`stage = plan`、`uses` 属于明确的 Inline Agent Action 集合
（当前为 `mohist/opencode`、`mohist/pi`），且当前 issue 仍有可解析父 issue 时才附加；
checks、其它 stage、其它 Action、AgentJob 与普通 issue 均不附加。

父背景是 HTTP 派发响应的可选执行上下文，不进入 Workflow `WorkDispatch`、WorkflowRun
metadata/state、task `with`、Variables 或 Prompts，也不新增模板表达式命名空间。Runner 只
负责透传；所选 Inline Agent Action 在每次适用 turn 中，把 JSON 编码的父标题与 body 作为
只读背景置于已解析 task prompt 之前，并明确当前子 issue body 是交付范围权威。无父背景时，
已解析 prompt 保持不变。

Repository 不进入 WorkflowRun snapshot 或 Run Variables。Issue 只保存目标仓库的资源名；
dispatch 使用该引用读取 Project Repository resource。未完成 Issue 会阻止目标 Repository
的 git 地址或 base branch 被修改，因此同一个 WorkflowRun 的各次 dispatch 读取稳定的
执行属性，而 WorkflowRun 不需要复制它们。完整规则见 [`../repositories.md`](../repositories.md)。

## Status

当前 Server 在生成 `WorkDispatch` 时先展开 `with` / `expect`，Runner 又把该展开值用于
recovery continuation。这样 `retrySelf` 和后续人工 retry 会把旧 Variables 固化为新的 task
declaration。issue #465 负责分离原始 declaration 与 attempt rendered input，并把 Runner
收敛为唯一 task input 展开边界。
