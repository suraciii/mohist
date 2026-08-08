# `mohist/opencode` Action

`mohist/opencode` 是 OpenCode Action：它把一次工作交给 OpenCode，并把执行
事实报告回来。Workflow 直接使用它时形成 Inline Agent，但 Action 本身不是 Agent，
也不会查找或启动 Mohist Agent。

Agent、AgentJob 和 AgentSession 的总体关系见 [Agent 与 AgentSession](../agent-sessions.md)。

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

逻辑 `session` 名称语义、物理 Session 复用不变量、缺失自动恢复、收尾执行与并发
规则与 `mohist/pi` 共享，见
[Action 契约](README.md#agent-执行类-action-的共享语义)。本 Action 的物理 Session
是 OpenCode Session；自动恢复以 OpenCode 明确报告 Session 不存在为准。

## OpenCode Session 操作

Follow-up、Compact、Reset 的行为与恢复规则和 `mohist/pi` 共享，见
[Action 契约](README.md#agent-执行类-action-的共享语义)；操作对象是当前绑定的
OpenCode Session。

## 完成与失败

完成判断、promise Action Output、执行期限、provider 额度耗尽与中断确认的共享语义
见 [Action 契约](README.md#agent-执行类-action-的共享语义)。

OpenCode 的权限配置仍是最终判断。它已经允许的操作直接执行，明确拒绝的操作仍然
拒绝。当 OpenCode 只要求确认时，Mohist 的无人值守执行仅允许这一次操作，不保存为
以后自动允许，也不会创建审批或要求用户介入。若这次回应无法完成，本次 task 立即
失败并给出可操作的错误，不等待执行期限耗尽。

## OpenCode 责任边界

安装者负责提供可用的 OpenCode CLI，以及配置 provider、插件和权限。Mohist 不安装、
升级或锁定 OpenCode CLI 的精确版本；启动时只验证当前
环境是否可用，并在不兼容时阻止 Runner 接收新工作。

工具的超时和重试由 OpenCode 判断。Mohist 只负责整个执行的期限和中断确认，不为单个
工具建立另一套超时策略。

Mohist 展示的模型列表用于帮助配置。最终模型是否合法、默认模型是什么，仍由
OpenCode 判断。

## 错误码

六个共享业务错误码与平台错误见
[Action 契约](README.md#agent-执行类-action-的共享语义)。`mohist/opencode` 另有：

| 错误码 | 含义 |
|---|---|
| `incompatible-runtime` | OpenCode 版本或数据与 Mohist 不兼容 |
| `permission-required` | 需要权限才能继续 |
| `interrupted` | 执行被 Runner 外部信号中断 |

## 实装差距

`mohist/opencode` 已经在 Workflow 与 AgentJob 两条来源落地：执行由 OpenCode 直接
驱动，内置 Profile 已切换到该 Action；Workflow 与 Agent 来源的配置、Session、
命令结果与诊断不再包含历史 ACP 身份字段。

稳定的 Session 身份、来源解析、Follow-up 与 Cancel 已经落地。Compact 和 Reset 的
产品入口已经存在，但 OpenCode 当前还不能执行这两个命令。

缺失的 OpenCode Session 目前仍会让部分新输入失败，自动重建、重新绑定和可用的
OpenCode Reset 尚未落地；对应实施 issue 待从本 spec 创建。Compact 是另一项已有实装
差距，不属于缺失恢复的实施范围。
