# Task Dispatch

## Task configuration expansion

`tasks[*].with` 和 task-level `expect` 可以包含 `${{ }}` 表达式。Workflow 在 dispatch 前
展开 runtime context 与 `${{ vars.* }}`；`${{ prompts.<key> }}` 保留为 Project Prompt
引用，由 Runner 在执行时解析。

```text
${{ vars.path }} 占据整个值 -> 替换为变量值并保留 JSON 类型
其他可解析表达式          -> 替换为对应 dispatch context
${{ prompts.<key> }}       -> 保留 Prompt key 引用
普通值                    -> 原样保留
```

Prompt body 不属于持久化的 task input。Runner 每次实际执行 task 时按 key 读取最新 body；
redelivery 和 retry 都会重新读取。

Variables 的解析、deep merge 与动态生效语义见 [`variables.md`](variables.md)。展开后的
`with` 是 Action 唯一的变量与配置输入；Action 不再次读取 Variables resource。

`expect` 单独展开并随 dispatch 发送，作为 Workflow 拥有的 task 完成契约。它不进入
`with`，也不属于 Runtime-specific Action Input。

## Dispatch context

`with` 和 `expect` 可以引用：

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

Runtime context、Workflow Variables 与 Project Prompts 是三个独立命名空间。完整的
dispatch/report 流程见 [`../runner.md`](../runner.md)。

Repository 不进入 WorkflowRun snapshot 或 Run Variables。Issue 只保存目标仓库的资源名；
dispatch 使用该引用读取当时的 Project Repository resource。Project 更新 git 地址或 base
branch 后，尚未 dispatch 的 task 使用更新后的资源值。
