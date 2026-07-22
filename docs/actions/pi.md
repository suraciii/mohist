# `mohist/pi` Action

`mohist/pi` 是 Pi Action：它把一次工作交给 Pi，并把执行事实报告回来。它与
[`mohist/opencode`](opencode.md) 处于同一层——Workflow 用 `uses` 选择其中一个执行
后端，两者不互相包装，也不共享输入。Workflow 直接使用它时形成 Inline Agent，但
Action 本身不是 Agent，也不会查找或启动 Mohist Agent。

Agent、AgentJob 和 AgentSession 的总体关系见 [Agent 与 AgentSession](../agents.md)。

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
（例如为 Mohist Agent 准备的 `runtime`）会被忽略并记入诊断，不会导致回合失败。

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
| `timeout` | 否 | `3600000` | 回合期限，以毫秒为单位；到达后中断当前回合 |

工具、技能、系统提示词和自动压缩继续使用 Pi 自己的配置，不复制成 Mohist 字段。
Action Input 不需要 `agent`、`kind` 或 `type`；使用哪个执行后端已经由 `uses` 决定。

Action Input 展开后的值是本次执行的唯一配置事实。`mohist/pi` 不会在后台额外读取
`vars.agent`。

## Workflow Session

`session` 标识 Workflow 来源的逻辑 AgentSession，语义与 `mohist/opencode` 一致：
同一 WorkflowRun 中使用相同名称的 task 解析到同一个逻辑 AgentSession；只要当前物理
绑定不变，它们共享对话上下文。Runtime 切换仍保留逻辑身份和 lineage，但新物理 Session
从空上下文开始，不迁移旧 Runtime 对话。不同名称相互隔离；省略时使用 Work ID。

### 物理 Session 复用不变量

同一 WorkflowRun 中，只要 task 指定同一个 `session` 名称，Mohist 就必须继续使用该
AgentSession 当前绑定的同一个物理 Pi Session。task 变化、task 重试，以及
`options.model` 或 `options.variant` 变化都不能替换这个物理 Session；模型选择只影响
本次回合，并在原 Session 上生效。

| 变化 | 物理 Pi Session |
|---|---|
| 后续 task 或重试继续使用同名 `session` | 保持不变 |
| `options.model` 或 `options.variant` 变化 | 保持不变 |
| Compact | 保持不变 |
| Reset | 建立新的空 Session，并记录会话沿革 |
| 工作目录变化 | 拒绝执行；需要新的逻辑 `session` 名称 |
| 执行后端变化 | 建立新物理 Session，并记录会话沿革 |

如果已绑定的物理 Session 无法继续，Mohist 必须明确失败并提示 Reset，不能静默建立
新的物理 Session。不同 `session` 名称仍相互隔离，不能因为 prompt、模型或配置相同而
合并。

如果 task 已完成工作但还有改动需要提交或还原，Mohist 会在同一个 AgentSession 和
物理 Pi Session 中继续这个收尾回合。这个收尾不会替换会话，也不要求用户先 Reset；
完成后，后续使用同名 `session` 的 task 继续沿用原对话上下文。

同一 AgentSession 同时只执行一个由 Workflow 发起的回合。不同 AgentSession 可以并行。
用户在 Session 页面提交的 follow-up 是例外：当前回合仍在执行时，它会进入当前回合；
Session 空闲时，它会开始下一回合。

Session 用量分别记录 input、output、cache read、cache write 与 thought tokens（Pi 提供时）；
cache write 不会并入 cache read，也不会因事件重投而重复累加。

## Pi Session 操作

当 AgentSession 当前绑定 Pi 时，Session 页面和对应 CLI 命令按以下方式执行：

| 操作 | 结果 |
|---|---|
| Follow-up | 把用户文本交给当前 Pi Session；确认 Pi 已接收后返回 |
| Compact | 使用 Pi 原生压缩当前 Session；Runtime Session 身份不变 |
| Reset | 在 Session 空闲时建立一个没有旧上下文的新 Pi Session；旧 Session 保留在会话沿革中 |

Compact 是用户在 Session 中发起的操作，不是 Workflow Action。Mohist 不生成一段假的
摘要来模拟压缩，也不会在 Pi 压缩失败时静默降级。

Runner 重启后，Mohist 仍使用 AgentSession 保存的 Pi Session 绑定继续这些操作。如果
对应的 Runtime Session 已不存在，操作明确失败，并提示用户 Reset；不会悄悄创建一段
看似连续的新对话。

## 完成与失败

Pi 回合成功结束后，Workflow 才按 task 的 `expect`、`artifacts`、`failIf` 和 recovery
规则判断后续流程。回合失败、取消或超时时，原始错误就是 task 结果，不再检查文件或
marker。这些是 Workflow 的 task 完成要求，不是 `mohist/pi` 的 Action Input。
Action Output 与 `mohist/opencode` 相同：只在命中 promise marker 时返回
`{ "promise": "..." }`，否则为 `null`。

Pi 无人值守执行时不会被工具确认阻塞：Pi 不在单次工具执行前要求批准，已配置允许
的操作直接执行。每次 Workflow Prompt 回合的期限默认固定为 60 分钟，可通过
Action Input 的 `timeout` 字段覆盖，从向 Pi 提交 Prompt 前开始计时；绑定和审计
输入准备不占用该预算，收尾 Prompt 是新的回合并获得新的 60 分钟。期限到达会中断
当前回合。提交结果不确定时不会自动重放 Prompt，避免同一任务被执行两次。Runner
主动触发的执行期限会明确报告 timeout；中断 Pi 只是收尾，不能用缺少 marker 覆盖
timeout，也不会替换当前 Session 绑定或自动 Reset。

provider 明确报告周、月、套餐额度，余额或计费耗尽时，Mohist 中断当前 Pi 回合并让
本次 task 失败，不等待 provider 继续重试。AgentSession 与当前物理 Pi Session 的绑定
保持不变；Session 回到空闲后，可以选择其他模型继续，无需 Reset。只有当前物理
Session 已不存在，或用户明确要求清空上下文时才使用 Reset。

如果 Mohist 无法确认当前回合已经停止，则明确报告中断未确认；不会把仍可能执行的回合
显示为已经安全停止。

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

工具的超时和重试由 Pi 判断。Mohist 只负责整个回合的期限和中断确认，不为单个工具
建立另一套超时策略。

## 错误码

`mohist/pi` 的业务失败用以下稳定错误码标识，供 recovery `when: error.code=...`
匹配；错误文案面向人，不用于匹配：

平台也可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、
未预期平台故障和期限失败；它们不属于本 Action 的业务错误。

| 错误码 | 含义 |
|---|---|
| `runtime-unavailable` | Pi 执行能力尚未就绪或不可用 |
| `session-workspace-mismatch` | Session 绑定的工作目录与本次执行不一致 |
| `session-binding-failed` | 逻辑 Session 绑定的解析或持久化失败 |
| `runtime-session-missing` | 绑定的 Pi Session 已不存在，需要 Reset |
| `unavailable-runtime` | Pi 报告不可用 |
| `turn-failed` | 回合执行失败（含 provider 额度、余额或计费耗尽） |

## 实装差距

直接 Workflow 路径已经实装：`uses: mohist/pi` 的 task/check 可执行回合，复用
Workflow AgentSession，并在现有 Session 页面展示 transcript、工具、状态、压缩、模型、
用量、成本与 lineage。以下能力仍属于后续工作：

- Mohist Agent 的 AgentJob 执行后端选择仍未实装，当前 Mohist Agent 固定使用 OpenCode。
- Pi 的 Follow-up、Compact、Reset、Cancel 等 Session 命令仍未实装。
- 面向 runtime 的模型 catalog 与 Web 模型选择 UI 仍未实装。
