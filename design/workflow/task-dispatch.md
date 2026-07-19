# Task Dispatch

## Task configuration expansion

`tasks[*].with` 和 task-level `expect` 可以包含 `${{ }}` 表达式。Workflow 在 dispatch 前
展开 runtime context 与 `${{ vars.* }}`；`${{ prompts.<key> }}` 保留为 Project Prompt
引用，由 Runner 在执行时解析。

```text
${{ vars.path }} 占据整个值 -> 替换为变量值并保留 JSON 类型
其他可解析表达式          -> 替换为对应 dispatch context
${{ prompts.<key> }}       -> 保留 Prompt key 引用
字符串内嵌入的表达式       -> 值转文本拼入；解析不出值或值为对象/数组 -> 任务失败
普通值                    -> 原样保留
```

嵌入拼接与 `\${{` 转义的作者可见语法以
[docs 的模板表达式节](../../docs/workflow-definition.md#模板表达式) 为权威。

Prompt body 不属于持久化的 task input。Runner 每次实际执行 task 时按 key 读取最新 body；
redelivery 和 retry 都会重新读取。

`${{ failure.* }}` 例外：runner 构造恢复任务时就地展开——触发恢复的任务输出只在 runner
手上。插入引擎的恢复任务不再含该表达式，其余表达式与普通任务相同，dispatch 前展开。

Variables 的解析、跨 scope merge 与动态生效语义见 [`variables.md`](variables.md)。
展开后的 `with` 是 Action 唯一的变量与配置输入；Action 不再次读取 Variables
resource。`with` 内部不再做 same-key deep-merge，配置进入 Action 只能通过显式
`options: ${{ vars.agent }}` 等整值 `${{ vars.* }}` 绑定。

`expect` 单独展开并随 dispatch 发送，作为 Workflow 拥有的 task 完成契约。它不进入
`with`，也不属于 Runtime-specific Action Input。

Runner 为每个 WorkItem 只解析一次 workspace，并把结果作为 `ActionContext.workDir` 交给
Action。Action 不得从 `variables.workspace.path` 重新选择工作目录；后者只是 dispatch
context 的可见事实，不是第二个执行入口。

持久 WorkItem 的 `uses` / `with` 若违反所选 Action 的静态输入契约，属于不可重试的
dispatch 拒绝。工作一旦 claim，DispatchService 必须用精确的 `workerId + workId` 让
WorkflowRun 将该 TaskRun 记为 Failed；只有普通渲染故障继续通过 poll redelivery 重试。

## Dispatch context

作者可见的命名空间清单以 [docs 的模板表达式表](../../docs/workflow-definition.md#模板表达式)
为权威；本表只补充实现侧来源：

| Variable | Source |
|---|---|
| `workflow.runId` | dispatch |
| `stage.name` | dispatch |
| `work.id` | dispatch |
| `issue.number` | dispatch |
| `repository.*` | Issue 的目标仓库引用；dispatch 时从 Project Repository resource 解析 |
| `workspace.*` | Runner 执行时解析的 workspace |
| `vars.*` | Effective Stage Variables |
| `tasks.<id>.outputs.*` | previous task output |
| `prompts.<key>` | Project Prompt；Runner 在执行时按 key 解析 |
| `failure.output` | 触发恢复的任务输出；runner 构造恢复任务时展开，仅恢复任务可用 |

Runtime context、Workflow Variables 与 Project Prompts 是三个独立命名空间。完整的
dispatch/report 流程见 [`../runner.md`](../runner.md)。

Repository 不进入 WorkflowRun snapshot 或 Run Variables。Issue 只保存目标仓库的资源名；
dispatch 使用该引用读取 Project Repository resource。未完成 Issue 会阻止目标 Repository
的 git 地址或 base branch 被修改，因此同一个 WorkflowRun 的各次 dispatch 读取稳定的
执行属性，而 WorkflowRun 不需要复制它们。完整规则见 [`../repositories.md`](../repositories.md)。
