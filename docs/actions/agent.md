# `mohist/agent` Action

`mohist/agent` 让一个 Workflow task 引用 Project 内预定义的 Mohist Agent 来执行：
task 获得该 Agent 的指令与执行配置快照，按 Inline Agent 同一套机制运行。
它只支持 task，不支持 workflow check。

它是**定义引用，不是工作委托**：不启动 AgentJob，工作的成功失败仍由 TaskRun
裁定，AgentSession 仍是 Workflow 来源。Agent、AgentJob 和 AgentSession 的总体
关系见 [Agent 与 AgentSession](../agent-sessions.md)。

## 基本用法

```yaml
- id: review
  uses: mohist/agent
  with:
    name: reviewer
    prompt: ${{ prompts.review }}
```

`name` 指向的 Agent 提供身份指令、执行后端（OpenCode 或 Pi）、模型、Variant 与 Skills；
`prompt` 是本次任务输入。适合同一个「角色」被多个 task、多个 Profile 复用，
或要和路由规则、`@` 提及共用同一个 Agent 身份的场景；一次性任务继续用
[`mohist/opencode`](opencode.md) 或 [`mohist/pi`](pi.md) 内联。

## Action 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `name` | 是 | — | Mohist Agent 的静态名称或 id；不支持模板表达式 |
| `prompt` | 是 | — | 本次交给该 Agent 的任务输入，支持模板表达式 |
| `session` | 否 | — | WorkflowRun 内的逻辑 Session 名称；省略时使用当前 Work ID |
| `timeout` | 否 | 与后端 Action 相同 | 本次执行的期限 |

执行后端、模型、Variant 与 Skills 由 Agent 配置决定，task 不覆盖；`prompt` 只是本次工作的
目标输入，不能修改 Agent 定义。`expect`、`artifacts`、`setVars`
与 recovery 等 task 级构造的行为与其它 Action 相同。

`name` 的解析顺序与 `mo` 命令面相同：以 `agent_` 开头的引用只按 id
解析；其它引用先按名称解析，名称未命中时再按 id 解析。

## 解析与快照

- `name` 在**每次 dispatch 时**解析为当时定义的 snapshot：指令、执行后端、模型、Variant
  与有序 Skills 随该 attempt 固定。
- 编辑 Agent 不影响已 dispatch 的 attempt；retry 重新解析——修复定义后 retry
  立即生效。
- 普通客户端可以提供 prompt 和上下文，但不能通过 task input 或上下文选择另一个 Runtime、
  Model、Variant 或 Skills。
- Profile 保存与 `mo workflow validate` 只校验输入形状（`name`、`prompt` 必填），
  不校验 Agent 是否存在——Profile 的生命周期不被 Agent 的增删卡住。

## 失败语义

| 错误码 | 含义 |
|---|---|
| `agent_not_found` | dispatch 时 `name` 不存在或 Agent 已归档 |

执行期错误（后端不可用、超时等）与所选执行后端的 Action 相同，recovery 的
`when` 匹配同样适用。

`mohist/agent` 仅能用于 task；用于 check 时会被拒绝。引用的 Agent 不存在或已归档时，
dispatch 失败码为 `agent_not_found`。
