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

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `prompt` | 是 | — | 本次交给 OpenCode 的提示词 |
| `session` | 否 | — | WorkflowRun 内的逻辑 Session 名称；省略时使用当前 Work ID |
| `options` | 否 | — | 本次选择 OpenCode 模型的对象 |
| `options.model` | 否 | — | OpenCode 模型，使用 `provider/model` 标识 |
| `options.variant` | 否 | — | 该模型的 OpenCode `variant` |
| `timeout` | 否 | `3600000` | 本次执行的期限，以毫秒为单位；到达后中断当前执行 |

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
本次执行，并在原 Session 上生效。

| 变化 | 物理 OpenCode Session |
|---|---|
| 后续 task 或重试继续使用同名 `session` | 保持不变 |
| `options.model` 或 `options.variant` 变化 | 保持不变 |
| Compact | 保持不变 |
| Reset | 建立新的空 Session；AgentSession 保留已有会话内容 |
| 提交新的独立输入前明确确认当前 Session 已不存在 | 自动建立新的空 Session |
| 工作目录变化 | 拒绝执行；需要新的逻辑 `session` 名称 |
| 执行后端变化 | 建立新的空物理 Session |

自动恢复只处理负责当前绑定的 Runner 上，OpenCode 明确确认旧 Session 已不存在、且本次
输入尚未被接受的情况。请求落到其它 Runner、OpenCode 暂时不可用、响应无法判断，或
Prompt 可能已经提交时，Mohist 明确失败，不替换绑定或重放 Prompt。新 Session 没有旧
上下文；同一 AgentSession 继续显示已有消息，并以“上下文已重置”说明后续从空上下文开始，
不维护物理 Session 历史。不同 `session` 名称仍相互隔离，不能因为 prompt、模型或配置
相同而合并。

如果 task 已完成工作但还有改动需要提交或还原，Mohist 会在同一个 AgentSession 和
物理 OpenCode Session 中继续这次收尾执行。这个收尾不会替换会话，也不要求用户先
Reset；完成后，后续使用同名 `session` 的 task 继续沿用原对话上下文。

同一 AgentSession 同时只执行一个由 Workflow 发起的输入。不同 AgentSession 可以并行。
用户在 Session 页面提交的 follow-up 是例外：Session 正在执行时，它会加入当前执行；
Session 空闲时，它会开始新的执行。

## OpenCode Session 操作

当 AgentSession 当前绑定 OpenCode 时，Session 页面和对应 CLI 命令按以下方式执行：

| 操作 | 结果 |
|---|---|
| Follow-up | 把用户文本交给当前 OpenCode Session；确认 OpenCode 已接收后返回 |
| Compact | 使用 OpenCode 原生压缩当前 Session；Runtime Session 身份不变 |
| Reset | 在 Session 空闲时建立一个没有旧上下文的新 OpenCode Session；AgentSession 保留已有会话内容 |

Compact 是用户在 Session 中发起的操作，不是 Workflow Action。Mohist 不生成一段
假的摘要来模拟压缩，也不会在 OpenCode 压缩失败时静默降级。

Runner 重启后，Mohist 仍使用 AgentSession 保存的 OpenCode Session 绑定继续这些
操作。如果提交新的独立输入前确认该 Session 已不存在，Workflow task、AgentJob 或空闲
Follow-up 会先自动建立并绑定新的空 Session。Compact 和针对执行中操作的命令不会这样
恢复；Reset 即使在旧 Session 已不存在时仍可建立新的空 Session。

## 完成与失败

OpenCode 执行成功结束后，Workflow 才按 task 的 `expect`、`artifacts`、`failIf` 和
recovery 规则判断后续流程。执行失败、取消或超时时，原始错误就是 task 结果，不再检查
文件或 marker。这些是 Workflow 的 task 完成要求，不是
`mohist/opencode` 的 Action Input。Action Output 只在命中 promise marker 时返回：

```json
{ "promise": "PASS" }
```

没有 promise marker 时，Action Output 为 `null`。Session ID、模型、用量、完整文本、
校验明细和错误详情属于 Session 或任务状态，不重复塞进 Action Output。

OpenCode 的权限配置仍是最终判断。它已经允许的操作直接执行，明确拒绝的操作仍然拒绝。
当 OpenCode 只要求确认时，Mohist 的无人值守执行仅允许这一次操作，不保存为以后自动
允许，也不会创建审批或要求用户介入。若这次回应无法完成，本次 task 立即失败并给出
可操作的错误，不等待执行期限耗尽。执行超时会中断当前执行；提交结果不确定时不会
自动重放 Prompt，避免同一任务被执行两次。Runner 主动触发的执行期限会明确报告 timeout；
中断 OpenCode 只是收尾，不能用缺少 marker 覆盖 timeout，也不会替换当前 Session 绑定或
自动 Reset。

provider 明确报告周、月、套餐额度，余额或计费耗尽时，Mohist 中断当前 OpenCode
执行并让本次 task 失败，不等待 provider 继续重试。AgentSession 与当前物理
OpenCode Session 的绑定保持不变；Session 回到空闲后，可以选择其他模型继续，无需
Reset。Reset 只表达用户明确要求清空上下文；物理 Session 缺失由下一条安全的独立输入
自动恢复。

如果 Mohist 无法确认当前执行已经停止，则明确报告中断未确认；不会把仍可能执行的
Session 显示为已经安全空闲。

## OpenCode 责任边界

安装者负责提供可用的 OpenCode CLI，以及配置 provider、插件和权限。Mohist 不安装、
升级或锁定 OpenCode CLI 的精确版本；启动时只验证当前
环境是否可用，并在不兼容时阻止 Runner 接收新工作。

工具的超时和重试由 OpenCode 判断。Mohist 只负责整个执行的期限和中断确认，不为单个
工具建立另一套超时策略。

Mohist 展示的模型列表用于帮助配置。最终模型是否合法、默认模型是什么，仍由
OpenCode 判断。

## 错误码

`mohist/opencode` 的业务失败用以下稳定错误码标识，供 recovery `when: error.code=...`
匹配；错误文案面向人，不用于匹配：

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、
未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

| 错误码 | 含义 |
|---|---|
| `runtime-unavailable` | OpenCode 执行能力尚未就绪或不可用 |
| `session-workspace-mismatch` | Session 绑定的工作目录与本次执行不一致 |
| `session-binding-failed` | 逻辑 Session 绑定的解析或持久化失败 |
| `runtime-session-missing` | OpenCode Session 已不存在，但当前操作无法安全地自动重建或重新投递 |
| `unavailable-runtime` | OpenCode 报告不可用 |
| `incompatible-runtime` | OpenCode 版本或数据与 Mohist 不兼容 |
| `permission-required` | 需要权限才能继续 |
| `interrupted` | 执行被 Runner 外部信号中断 |
| `execution-failed` | 执行失败（含 provider 额度、余额或计费耗尽） |

## 实装差距

`mohist/opencode` 已经在 Workflow 与 AgentJob 两条来源落地：执行由 OpenCode 直接
驱动，内置 Profile 已切换到该 Action；Workflow 与 Agent 来源的配置、Session、
命令结果与诊断不再包含历史 ACP 身份字段。

稳定的 Session 身份、来源解析、Follow-up 与 Cancel 已经落地。Compact 和 Reset 的
产品入口已经存在，但 OpenCode 当前还不能执行这两个命令。

缺失的 OpenCode Session 目前仍会让部分新输入失败，自动重建、重新绑定和可用的
OpenCode Reset 尚未落地；对应实施 issue 待从本 spec 创建。Compact 是另一项已有实装
差距，不属于缺失恢复的实施范围。
