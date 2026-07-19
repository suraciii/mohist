# `mohist/opencode` Action

`mohist/opencode` 是 OpenCode Action：它把一次工作交给 OpenCode，并把执行
事实报告回来。Workflow 直接使用它时形成 Inline Agent，但 Action 本身不是 Agent，
也不会查找或启动 Mohist Agent。

Agent、AgentJob 和 AgentSession 的总体关系见 [Agent 与 AgentSession](../agents.md)。

## 基本用法

最小配置只有提示词：

```yaml
- id: proposal
  uses: mohist/opencode
  with:
    prompt: ${{ prompts.proposal }}
```

需要让 Project 或 Issue 调整 OpenCode 配置时，先在独立的 Variables 中设置：

```yaml
vars:
  agent:
    model: anthropic/claude-sonnet-4
    variant: high
```

再由 Workflow Profile 显式绑定 `session` 和 `options`：

```yaml
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
同一对象放在 Project、Issue 或 Run Variables，也可以直接内联到 task。Issue 的值覆盖
Project，Run 的值覆盖 Issue；Workflow Profile 只引用变量，不保存变量值。

`agent` 是现有 Workflow 变量名；在这个 Action 中，它只提供 `model` 和 `variant`，
不表示 Mohist Agent 身份，也不会选择 OpenCode agent。

Action Input 展开后的值是本次执行的唯一配置事实。`mohist/opencode` 不会在后台
额外读取 `vars.agent`；没有显式绑定 `options` 时，使用当前 OpenCode Session 的
选择，首次执行则使用 OpenCode 默认值。

## Action 输入

| 字段 | 必填 | 含义 |
|---|---:|---|
| `prompt` | 是 | 本次交给 OpenCode 的提示词 |
| `session` | 否 | WorkflowRun 内的逻辑 Session 名称；省略时使用当前 Work ID |
| `options` | 否 | 本次选择 OpenCode 模型的对象 |
| `options.model` | 否 | OpenCode 模型，使用 `provider/model` 标识 |
| `options.variant` | 否 | 该模型的 OpenCode `variant` |

工具、插件、权限、默认执行方式和自动压缩继续使用 OpenCode 自己的配置，不复制成
Mohist 字段。Action Input 不需要 `agent`、`kind` 或 `type`；使用哪个执行后端已经
由 `uses` 决定。

## Workflow Session

`session` 标识 Workflow 来源的逻辑 AgentSession。同一 WorkflowRun 中使用相同名称
的 task 共享对话上下文；不同名称相互隔离。省略 `session` 时使用 Work ID，避免
无意间把两个 task 放进同一段对话。

### 物理 Session 复用不变量

同一 WorkflowRun 中，只要 task 指定同一个 `session` 名称，Mohist 就必须继续使用该
AgentSession 当前绑定的同一个物理 OpenCode Session。task 变化、task 重试，以及
`options.model` 或 `options.variant` 变化都不能替换这个物理 Session；模型选择只影响
本次回合，并在原 Session 上生效。

| 变化 | 物理 OpenCode Session |
|---|---|
| 后续 task 或重试继续使用同名 `session` | 保持不变 |
| `options.model` 或 `options.variant` 变化 | 保持不变 |
| Compact | 保持不变 |
| Reset | 建立新的空 Session，并记录会话沿革 |
| 工作目录或执行后端变化 | 建立新 Session，并记录会话沿革 |

如果已绑定的物理 Session 无法继续，Mohist 必须明确失败并提示 Reset，不能静默建立
新的物理 Session。不同 `session` 名称仍相互隔离，不能因为 prompt、模型或配置相同而合并。

如果 task 已完成工作但还有改动需要提交或还原，Mohist 会在同一个 AgentSession 和
物理 OpenCode Session 中继续这个收尾回合。这个收尾不会替换会话，也不要求用户先
Reset；完成后，后续使用同名 `session` 的 task 继续沿用原对话上下文。

同一 AgentSession 同时只执行一个由 Workflow 发起的回合。不同 AgentSession 可以并行。
用户在 Session 页面提交的 follow-up 是例外：当前回合仍在执行时，它会进入当前
回合；Session 空闲时，它会开始下一回合。

## OpenCode Session 操作

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

OpenCode 的权限配置仍是最终判断。它已经允许的操作直接执行，明确拒绝的操作仍然拒绝。
当 OpenCode 只要求确认时，Mohist 的无人值守执行仅允许这一次操作，不保存为以后自动
允许，也不会创建审批或要求用户介入。若这次回应无法完成，本次 task 立即失败并给出
可操作的错误，不等待执行期限耗尽。执行超时会中断当前回合；提交结果不确定时不会
自动重放 Prompt，避免同一任务被执行两次。

provider 明确报告周、月、套餐额度，余额或计费耗尽时，Mohist 中断当前 OpenCode
回合并让本次 task 失败，不等待 provider 继续重试。AgentSession 与当前物理
OpenCode Session 的绑定保持不变；Session 回到空闲后，可以选择其他模型继续，无需
Reset。只有当前物理 Session 已不存在，或用户明确要求清空上下文时才使用 Reset。

如果 Mohist 无法确认当前回合已经停止，则明确报告中断未确认；不会把仍可能执行的
回合显示为已经安全停止。

## OpenCode 责任边界

安装者负责提供可用的 OpenCode CLI，以及配置 provider、插件和权限。Mohist 不安装、
升级或锁定 OpenCode CLI 的精确版本；启动时只验证当前
环境是否可用，并在不兼容时阻止 Runner 接收新工作。

工具的超时和重试由 OpenCode 判断。Mohist 只负责整个回合的期限和中断确认，不为单个
工具建立另一套超时策略。

Mohist 展示的模型列表用于帮助配置。最终模型是否合法、默认模型是什么，仍由
OpenCode 判断。

## 实装差距

`mohist/opencode` 已经在 Workflow 来源落地：Workflow 的回合直接由 OpenCode 驱动，
内置 Profile 已切换到该 Action；Workflow 来源的配置、Session、命令结果与诊断
不再包含历史 ACP 身份字段。

issue-407 已交付稳定的 Session 身份、来源解析和 Compact、Reset、Follow-up、
Cancel 的命令语义；Session 在这些操作后仍保持同一身份。

issue-410 处理剩余的 AgentJob 路径清理，包括 Agent 来源 Session 中 ACP 痕迹与
ACP 依赖的最终移除。Workflow 来源的 Session 命令（Follow-up / Compact /
Reset / Cancel）当前仍由历史 ACP 路径承担；issue-409 内的 T-005 落地后，它们将
改走 OpenCode 并共享同一套 Session 身份。
