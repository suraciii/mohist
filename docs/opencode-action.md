# `mohist/opencode` Action

`mohist/opencode` 是 OpenCode Action：它把一次工作交给 OpenCode，并把执行
事实报告回来。Workflow 直接使用它时形成 Inline Agent，但 Action 本身不是 Agent，
也不会查找或启动 Mohist Agent。

Agent、AgentJob 和 AgentSession 的总体关系见 [Agent 与 AgentSession](agents.md)。

## 基本用法

最小配置只有提示词：

```yaml
- id: proposal
  uses: mohist/opencode
  with:
    prompt: ${{ prompts.proposal }}
```

需要复用上下文或选择 OpenCode 配置时，显式传入 `session` 和 `options`：

```yaml
variables:
  agent:
    model:
      providerID: anthropic
      id: claude-sonnet-4
      variant: high

stages:
  - stage: plan
    tasks:
      - id: proposal
        uses: mohist/opencode
        with:
          session: plan
          prompt: ${{ prompts.proposal }}
          options: ${{ vars.agent }}
```

`${{ vars.agent }}` 占据整个 `options` 值时，展开结果仍是一个对象。用户可以把
同一对象放在 template、project、issue 或其他变量层，也可以直接内联到 task；
Mohist 不规定配置必须来自哪一层。

Action Input 展开后的值是本次执行的唯一配置事实。`mohist/opencode` 不会在后台
额外读取 `vars.agent`；没有显式绑定 `options` 时，使用当前 OpenCode Session 的
选择，首次执行则使用 OpenCode 默认值。

## Action Input

| 字段 | 必填 | 含义 |
|---|---:|---|
| `prompt` | 是 | 本次交给 OpenCode 的提示词 |
| `session` | 否 | WorkflowRun 内的逻辑 Session 名称；省略时使用当前 Work ID |
| `options` | 否 | 本次选择 OpenCode agent 或 model 的对象 |
| `options.agent` | 否 | OpenCode 自己的 agent 名称 |
| `options.model` | 否 | OpenCode model；提供时必须同时包含 `providerID` 和 `id` |
| `options.model.variant` | 否 | 该模型的 OpenCode variant |

`options.agent` 是 OpenCode 配置，不是 Mohist Agent。工具、插件、权限和自动压缩
继续使用 OpenCode 自己的配置，不复制成 Mohist 字段。Action Input 不需要 `kind`
或 `type`；使用哪个执行后端已经由 `uses` 决定。

## Workflow Session

`session` 标识 Workflow 来源的逻辑 AgentSession。同一 WorkflowRun 中使用相同名称
的 task 共享对话上下文；不同名称相互隔离。省略 `session` 时使用 Work ID，避免
无意间把两个 task 放进同一段对话。

同一个 AgentSession 可以在不同 task 中切换 OpenCode agent、model 或
variant，不会因此丢失上下文。Reset、工作目录变化，或未来改用另一种执行后端时，
Mohist 会建立新的 Runtime Session，并把前后关系保存在同一个 AgentSession 的
会话沿革中；不会偷偷把上下文搬到另一个后端。

同一 AgentSession 同时只执行一个由 Workflow 发起的回合。不同 AgentSession 可以并行。
用户在 Session 页面提交的 follow-up 是例外：当前回合仍在执行时，它会进入当前
回合；Session 空闲时，它会开始下一回合。

## OpenCode-backed Session 操作

当 AgentSession 当前绑定 OpenCode 时，Session 页面和对应 CLI 命令按以下方式执行：

| 操作 | 结果 |
|---|---|
| Follow-up | 把用户文本交给当前 OpenCode Session；确认 OpenCode 已接收后返回 |
| Compact | 使用 OpenCode 原生压缩当前 Session；Runtime Session 身份不变 |
| Reset | 在 Session 空闲时建立一个没有旧上下文的新 OpenCode Session；旧 Session 保留在会话沿革中 |

Compact 是用户在 Session 中发起的操作，不是 Workflow Action。Mohist 不生成一段
假的摘要来模拟压缩，也不会在 OpenCode 压缩失败时静默降级。

Runner 重启后，Mohist 仍使用 AgentSession 保存的 OpenCode Session 绑定继续这些
操作。如果对应的 Runtime Session 已不存在，操作明确失败，并提示用户 Reset；
不会悄悄创建一段看似连续的新对话。

## 完成与失败

OpenCode 回合结束后，Workflow 仍按 task 的 `expect`、`artifacts`、`failIf` 和
recovery 规则判断后续流程。这些是 Workflow 的 task 完成要求，不是
`mohist/opencode` 的 Action Input。Action Output 只在命中 promise marker 时返回：

```json
{ "promise": "PASS" }
```

没有 promise marker 时，Action Output 为 `null`。Session ID、模型、用量、完整文本、
校验明细和错误详情属于 Session 或任务状态，不重复塞进 Action Output。

Mohist 不自动批准 OpenCode 权限请求。若 OpenCode 的权限配置仍要求无人值守流程中
无法完成的交互，本次 task 失败并给出可操作的错误。执行超时会中断当前回合；提交
结果不确定时不会自动重放 Prompt，避免同一任务被执行两次。

## OpenCode 责任边界

安装者负责提供可用的 OpenCode CLI，以及配置 provider、model、OpenCode agent、
插件和权限。Mohist 不安装、升级或锁定 OpenCode CLI 的精确版本；启动时只验证当前
环境是否可用，并在不兼容时阻止 Runner 接收新工作。

Mohist 展示的模型列表用于帮助配置。最终模型是否合法、默认模型是什么，仍由
OpenCode 判断。

## 实装差距

当前可用 Action 仍是 `mohist/acp-agent`，Action Input 仍包含旧的 `agent.type`
形状，Workflow schema 也仍把 `expect` 放在 `with` 中。Session Compact 尚未完全
采用这里定义的 OpenCode 原生语义。
`mohist/opencode`、新的 Session 身份以及本篇 Session 操作语义尚待实现。
