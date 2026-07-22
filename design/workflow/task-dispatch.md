# Task Dispatch

本文是 task 输入模板求值时机的单一权威。`tasks[*].with` 与 task-level `expect` 是
Workflow 声明的一部分；服务端按原始形态随 dispatch 一起发送，不预先展开模板；Runner
在调用 Action 之前的执行入口统一展开。Prompt body、Effective Stage Variables、
runtime context 与 failure context 是 attempt 不可变快照的组成部分。

## 渲染边界

模板求值只发生在 Runner 调用 Action 之前的执行入口，**不**发生在 Server dispatch 阶段：

- Server 持久化 task 时保存原始 `with` / `expect` 声明，不展开模板。
- 每次 dispatch 在 wire 上携带原始 `with` / `expect`，并附加该 attempt 的不可变上下文
  快照：
  - Effective Stage Variables（按 [`variables.md`](variables.md) 在 dispatch 时解析并冻结）；
  - 已加载的 Project Prompt body（按 key 在 dispatch 时读取）；
  - runtime context（`workflow.runId`、`stage.name`、`work.*`、`issue.*`、`repository.*`、
    `tasks.<id>.outputs.*`、`workspace.*`）；
  - 适用时的 failure context（`failure.*`，仅恢复任务）。
- Runner 在 manifest 校验之前、调用 Action 之前，从原始 `with` / `expect` 在该 attempt
  快照上渲染本次执行的局部 inputs。渲染产生新结构，**不**修改 dispatch 携带的原始
  `with` / `expect`，**不**写入持久化的 task 定义、Action `addTasks` 定义或 retry 来源。
- 渲染完成后再走 manifest 校验、`working-directory` 解析和 Action 调用。Action 只接收
  渲染并校验后的单一输入通道，**不**接收 raw input、Variables resource 或完整 dispatch
  context。

attempt 一经 dispatch，其上下文快照在该 attempt 生命周期内保持不变；后续对 Variables、
Prompts、Profile Definition 或 stage overlay 的修改只影响尚未 dispatch 的 task，以及后续
attempt（retry、recovery continuation、rerun-from-stage）。已经派发的 attempt 不因事后
修改而改变。

## 模板表达式规则

下表是所有 attempt 共享的求值规则，规则由 Runner 在渲染阶段执行；dispatch 阶段不参与
求值：

```text
${{ path }} 占据整个值      -> 替换为对应值并保留 JSON 类型
其他可解析表达式           -> 替换为对应 dispatch context
${{ prompts.<key> }}       -> dispatch 时已按 Project Prompt key 加载 body，此处只参与
                              与 `with` / `expect` 同一语法的求值
字符串内嵌入的表达式       -> 值转文本拼入；解析不出值或值为对象/数组 -> 任务失败
任一完整表达式解析不出值   -> 任务失败
普通值                    -> 原样保留
```

嵌入拼接与 `\${{` 转义的作者可见语法以
[docs 的模板表达式节](../../docs/workflow-definition.md#模板表达式) 为权威。

`expect` 渲染后只作为 Workflow 拥有的完成契约，不进入 Action 输入通道。

## Deferred 渲染

`render: deferred` 是 Action manifest 输入字段上的声明：被声明的字段在 Runner 渲染阶段
保持原值（含内部 `${{ ... }}`）原样进入 manifest 校验和 Action 调用；未声明 deferred 的
字段在 Runner 渲染阶段按上面的规则递归展开，包括嵌套的对象与数组。Action 只能从
deferred 字段读到保留的内部模板，**不**能从任何输入通道读到 raw `with` / `expect`、
Variables resource 或完整 dispatch context。

Runner 为每个 WorkItem 只解析一次 workspace，并把结果作为 `ActionContext.workDir` 交给
Action。Action 不得从 `variables.workspace.path` 重新选择工作目录；后者只是 dispatch
context 的可见事实，不是第二个执行入口。

持久 WorkItem 的 `uses` / `with` 若违反所选 Action 的静态输入契约，或 attempt context
无法完成模板展开，Runner 必须返回确定的 `invalid-input` 失败。已经 claim 的 TaskRun 必须
以精确的 `workerId + workId` 报告失败；不得靠 poll redelivery 重试同一份确定无效的输入。

## Prompt body 求值

Prompt body 不属于持久化的 task input。Server 在 dispatch 时按 `prompts.<key>` 把 body
加载进 attempt 快照；Runner 在渲染阶段对快照中的 body 做 `${{ ... }}` 求值，规则与
`with` / `expect` 共享同一语法与失败语义。redelivery、retry 与 rerun 都基于自己的快照
重新读取并渲染，因此一次 attempt 使用的 Prompt body 与该 attempt 的 dispatch 时刻绑定。

## Effective Variables 解析

Variables 的资源、跨 scope merge 与动态生效语义见 [`variables.md`](variables.md)。Server
在 dispatch 时按当前 Stage 解析 Effective Stage Variables 并冻结为该 attempt 快照的一部分；
Runner 不再读取 Variables resource，不在 dispatch 后拉取最新变量。`vars.*` 在 attempt
整个生命周期内只在 attempt 快照里出现一次。

`${{ failure.* }}` 由 Runner 构造恢复任务时就地展开——触发恢复的任务 output 只在 Runner
手上，见 [`recovery.md`](recovery.md)。其余表达式（包括恢复任务里未与触发 attempt 绑定
的其他 `vars.*`）继续保留原始声明，在该 attempt 的渲染阶段统一展开。

## Dispatch context

作者可见的命名空间清单以 [docs 的模板表达式表](../../docs/workflow-definition.md#模板表达式)
为权威；本表只补充实现侧来源与求值时机：

| Variable | Source | Timing |
|---|---|---|
| `workflow.runId` | dispatch | dispatch 时固定 |
| `stage.name` | dispatch | dispatch 时固定 |
| `work.*` | dispatch；包括 `id`、`type`、`title`、`attempt` | dispatch 时固定 |
| `work.approvalFeedback.*` | 仅由 ApprovalFeedback 产生的 task；包括 `id`、`stage`、`createdAt`、`summary` | dispatch 时固定 |
| `issue.*` | Issue context；包括 `projectId`、`number`、`title`、`body` | dispatch 时固定 |
| `repository.*` | Issue 的目标仓库引用；dispatch 时从 Project Repository resource 解析 | dispatch 时固定 |
| `workspace.*` | Runner 执行时解析的 workspace | Runner 执行入口 |
| `vars.*` | Effective Stage Variables | dispatch 时解析并冻结为 attempt 快照的一部分 |
| `tasks.<id>.outputs.*` | previous task output | dispatch 时固定 |
| `prompts.<key>` | Project Prompt body；按 key 读取 | dispatch 时加载进快照；Runner 渲染阶段求值 |
| `failure.output` | 触发恢复的任务 output；Runner 构造恢复任务时展开 | 仅恢复任务可用 |
| `failure.error.code` | 触发恢复的 error code | 仅恢复任务可用 |
| `failure.error.message` | 触发恢复的可操作错误文案 | 仅恢复任务可用 |

Runtime context、Workflow Variables 与 Project Prompts 是三个独立命名空间，attempt 快照中
保留各自的来源与求值时机。完整的 dispatch / report 流程见 [`../runner.md`](../runner.md)。

Effective Variables 只放在 `vars` 下，不把变量 key 复制成顶层裸名；Runtime context 也不
写回或合并进 Variables。`work.approvalFeedback` 只随由该反馈产生的 task 存在，普通 task
不携带。OpenSpec 目录不是 runtime context，由 Profile 与 Prompt 使用
`openspec/changes/issue-${{ issue.number }}` 明文表达。

## 校验时机

Profile 保存/更新时的 catalog 校验只检查常量输入与 Action 契约（未知 `uses`、未知输入键、
缺 `required`、常量输入的类型错），含模板表达式的输入只校验键名。dispatch 不再做
Server 侧展开，模板表达式的值校验、类型校验与 required 校验在 Runner 渲染并应用
manifest 后执行；不通过即 attempt 失败为 `invalid-input`，Action 不被调用。

持久 WorkItem 的 `uses` 在 dispatch 命中已退役 Action 时仍按 dispatch 拒绝处理：Runner
在 manifest 校验阶段识别到 tombstone，以 tombstone 指引文案失败为不可重试。

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
