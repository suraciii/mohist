# `mohist/pi` Action

`mohist/pi` 是 Pi Action：它把一次工作交给 Pi，并把执行事实报告回来。它与
[`mohist/opencode`](opencode.md) 处于同一层——Workflow 用 `uses` 选择其中一个执行
后端，两者不互相包装，也不共享输入。Workflow 直接使用它时形成 Inline Agent，但
Action 本身不是 Agent，也不会查找或启动 Mohist Agent。

Agent、AgentJob 和 AgentSession 的总体关系见 [Agent 与 AgentSession](../agent-sessions.md)。

## 基本用法

最小配置只有提示词：

```yaml
- id: proposal
  uses: mohist/pi
  with:
    prompt: ${{ prompts.proposal }}
```

模型选项的绑定方式与 `mohist/opencode` 相同：在 Project、Issue 或 Run Variables 中
设置 `agent` 对象，再由 Workflow Profile 显式绑定 `session` 和 `options`：

```yaml
vars:
  agent:
    model: anthropic/claude-sonnet-4
    variant: high

stages:
  - stage: plan
    tasks:
      - id: proposal
        uses: mohist/pi
        with:
          session: plan
          prompt: ${{ prompts.proposal }}
          options: ${{ vars.agent }}
```

`agent` 变量的合并规则（Issue 覆盖 Project，Run 覆盖 Issue）与其他 Workflow 变量
一致，见 [`mohist/opencode`](opencode.md) 的基本用法。同一个 `agent` 对象可以绑定给
任何一个后端 Action：对 `mohist/pi` 有效的键是 `model` 和 `variant`；对象中其余的键
（例如为 Mohist Agent 准备的 `runtime`）会被忽略并记入诊断，不会导致执行失败。

`${{ vars.agent }}` 占据整个 `options` 值时，展开结果仍是一个对象。没有显式绑定
`options` 时，使用当前 Pi Session 的模型选择，首次执行则使用 Pi 默认值。

## Action 输入

| 字段 | 必填 | 默认 | 含义 |
|---|---:|---|---|
| `prompt` | 是 | — | 本次交给 Pi 的提示词 |
| `session` | 否 | — | WorkflowRun 内的逻辑 Session 名称；省略时使用当前 Work ID |
| `options` | 否 | — | 本次选择 Pi 模型的对象 |
| `options.model` | 否 | — | Pi 模型，使用 `provider/model` 标识 |
| `options.variant` | 否 | — | 该模型的推理档位（Pi thinking level），如 `low`、`medium`、`high` |
| `timeout` | 否 | `3600000` | 本次执行的期限，以毫秒为单位；到达后中断当前执行 |

工具、技能、系统提示词和自动压缩继续使用 Pi 自己的配置，不复制成 Mohist 字段。
Action Input 不需要 `agent`、`kind` 或 `type`；使用哪个执行后端已经由 `uses` 决定。

Action Input 展开后的值是本次执行的唯一配置事实。`mohist/pi` 不会在后台额外读取
`vars.agent`。

## Workflow Session

逻辑 `session` 名称语义、物理 Session 复用不变量、缺失自动恢复、收尾执行与并发
规则与 `mohist/opencode` 共享，见
[Action 契约](README.md#agent-执行类-action-的共享语义)。本 Action 的物理 Session
是 Pi 的 session 文件；自动恢复以文件明确不存在为准，文件损坏或无法打开不算缺失。

Session 用量分别记录 input、output、cache read、cache write 与 thought tokens（Pi 提供时）；
cache write 不会并入 cache read，也不会因事件重投而重复累加。

## Pi Session 操作

Follow-up、Compact、Reset 的行为与恢复规则和 `mohist/opencode` 共享，见
[Action 契约](README.md#agent-执行类-action-的共享语义)；操作对象是当前绑定的
Pi Session。

## 完成与失败

完成判断、promise Action Output、执行期限、provider 额度耗尽与中断确认的共享语义
见 [Action 契约](README.md#agent-执行类-action-的共享语义)。

Pi 无人值守执行时不会被工具确认阻塞：Pi 不在单次工具执行前要求批准，已配置允许的
操作直接执行。

## Pi 责任边界

Pi 随 Mohist Runner 一起发布，版本由 Mohist 锁定；安装者不需要单独安装或升级 Pi。
这与 `mohist/opencode` 不同——OpenCode CLI 由安装者提供，Pi 则是 Runner 的内置能力。

安装者负责为 Runner 运行环境配置 provider 凭证（环境变量或 Pi 自己的登录凭证）。
Mohist 不管理 API key，也不在 UI 中收集凭证。模型是否可用、默认模型是什么，由 Pi
根据已配置的凭证判断；Mohist 展示的模型列表只用于帮助配置。

Pi 执行工具时不逐项征求批准，也不提供沙箱；工具以 Runner 进程的权限直接执行。为
保证无人值守执行的确定性，Mohist 不加载工作仓库中项目级的 Pi 配置（`.pi/` 目录中的
设置、扩展、技能等）；仓库无法通过携带 Pi 配置改变 Runner 的执行行为。仓库根部的
AGENTS.md 和 CLAUDE.md 不属于 Pi 配置，仍作为上下文提供给模型（与 OpenCode 的行为
一致）。需要自定义 Pi 行为时，在 Runner 用户的全局 Pi 配置中进行。

工具的超时和重试由 Pi 判断。Mohist 只负责整个执行的期限和中断确认，不为单个工具
建立另一套超时策略。

## 错误码

`mohist/pi` 的业务错误码即六个共享业务错误码，见
[Action 契约](README.md#agent-执行类-action-的共享语义)；无 Pi 特有业务错误码。

## 实装差距

Workflow 与 AgentJob 两条路径都已实装：Workflow 的 `uses: mohist/pi` 和选择 Pi 的 Mohist
Agent 都可执行输入，复用 AgentSession，并在现有 Session 页面展示 transcript、工具、
状态、压缩、模型、用量和成本。Agent 和 issue 的执行后端选择、按 Runtime 提供
模型目录与 Web 选择器也已落地。

缺失的 Pi Session 文件目前仍会让部分新输入失败，自动重建与重新绑定尚未落地；对应
实施 issue 待从本 spec 创建。
