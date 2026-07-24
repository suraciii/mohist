# Action 契约

Action 是 Workflow task 通过 `uses` 选择的一次执行接口。每个 Action 定义自己的
`with` 输入、输出和失败语义,但不拥有 Workflow 的完成判断,也不代表一个有身份的
Mohist Agent。

每个 Action 的契约是声明式的,包含三部分:

- **输入**:名称、是否必填、默认值。任务的 `with` 按声明校验——未知字段、缺少
  必填字段、类型不符都会被拒绝(保存 Profile 时即报错,而不是运行到才失败),
  不存在声明之外的隐藏输入。
- **输出**:成功时产出的字段,供 `setVars`、`${{ tasks.<id>.outputs.* }}` 和
  recovery 匹配读取。
- **错误码**:该 Action 全部业务失败的稳定标识目录,供 recovery
  `when: error.code=...` 匹配;错误文案面向人,不用于匹配。

平台还可能产生 `invalid-input`、`unexpected-error` 和 `timeout`，分别表示输入校验、
未预期平台故障和期限失败；它们不属于任何 Action 的业务错误。

本目录保存需要独立说明的 Action 产品契约。Workflow 的阶段、task、`expect` 和恢复
配置见 [Workflow Profile](../workflow-profiles.md);Action、Inline Agent 和 Mohist Agent
的关系见 [Agent 与 AgentSession](../agents.md)。

正文统一使用中文;产品中的规范术语、配置字段和命令保留原名。

## 当前 Action

- [`mohist/opencode`](opencode.md) —— 通过 OpenCode 执行一次输入,定义模型选项、
  Workflow Session 和 Session 操作语义。
- [`mohist/pi`](pi.md) —— 通过 Pi 执行一次输入;与 `mohist/opencode` 同层,共享
  模型选项形状与 Session 语义,但安装与信任边界不同。
- [`mohist/agent`](agent.md) —— 引用预定义 Mohist Agent 的定义执行 task：指令与
  配置来自 Agent 快照，工作机制与 Inline Agent 相同，不创建 AgentJob。

**Git Actions**:工作区准备、rebase、rebase 状态、merge readiness 和 push 的显式 `with`
输入契约。

- [`mohist/workspace-prepare`](git.md#mohistworkspace-prepare)
- [`mohist/rebase`](git.md#mohistrebase)
- [`mohist/rebase-status`](git.md#mohistrebase-status)
- [`mohist/merge-ready`](git.md#mohistmerge-ready)
- [`mohist/push`](git.md#mohistpush)

**GitHub PR Actions**:PR 创建、ready、checks、状态校验和 squash merge 的显式 `with`
输入契约。

- [`mohist/create-github-pr`](github-pr.md#mohistcreate-github-pr)
- [`mohist/mark-github-pr-ready`](github-pr.md#mohistmark-github-pr-ready)
- [`mohist/merge-github-pr`](github-pr.md#mohistmerge-github-pr)
- [`mohist/github-pr-checks`](github-pr.md#mohistgithub-pr-checks)
- [`mohist/github-pr-status`](github-pr.md#mohistgithub-pr-status)

**Core Actions**:进程、内联脚本、文件存在性检查和标记检查。

- [`core/process`](core.md#coreprocess)
- [`core/script`](core.md#corescript)
- [`core/artifact-exists`](core.md#coreartifact-exists)
- [`core/marker`](core.md#coremarker)

**OpenSpec Actions**:加载 `tasks.json`、核查 OpenSpec change 产物和归档 change。

- [`mohist/openspec-tasks`](openspec.md#mohistopenspec-tasks)
- [`mohist/openspec-artifacts`](openspec.md#mohistopenspec-artifacts)
- [`mohist/archive-change`](openspec.md#mohistarchive-change)

Pi 是同层的独立 Action,不是 `mohist/opencode` 的输入扩展。

## Agent 执行类 Action 的共享语义

`mohist/opencode` 与 `mohist/pi` 共享以下语义，各篇只写差异。`mohist/agent` 通过
Agent 定义引用落到同一类执行，同样遵循。

### Workflow Session

`session` 标识 Workflow 来源的逻辑 AgentSession。同一 WorkflowRun 中同名 task 共享
对话上下文，不同名称相互隔离；省略 `session` 时使用 Work ID，避免无意间把两个 task
放进同一段对话。执行后端切换保留逻辑身份，但新物理 Session 从空上下文开始，不迁移
旧对话，也不建立物理 Session 历史。

### 物理 Session 复用不变量

同一 WorkflowRun 中，只要 task 指定同一个 `session` 名称，Mohist 就必须继续使用该
AgentSession 当前绑定的同一个物理 Session。task 变化、task 重试、`options.model` 或
`options.variant` 变化都不能替换它；模型选择只影响本次执行，并在原 Session 上生效。

| 变化 | 物理 Session |
|---|---|
| 后续 task 或重试继续使用同名 `session` | 保持不变 |
| `options.model` 或 `options.variant` 变化 | 保持不变 |
| Compact | 保持不变 |
| Reset | 建立新的空 Session；AgentSession 保留已有会话内容 |
| 提交新的独立输入前明确确认当前 Session 已不存在 | 自动建立新的空 Session |
| 工作目录变化 | 拒绝执行；需要新的逻辑 `session` 名称 |
| 执行后端变化 | 建立新的空物理 Session |

自动恢复只处理负责当前绑定的 Runner 上、后端明确确认旧 Session 已不存在、且本次
输入尚未被接受的情况。请求落到其它 Runner、后端暂时不可用、响应无法判断，或
Prompt 可能已经提交时，Mohist 明确失败，不替换绑定或重放 Prompt。新 Session 没有旧
上下文；同一 AgentSession 继续显示已有消息，并以「上下文已重置」说明后续从空上下文
开始。

task 已完成工作但还有改动需要提交或还原时，Mohist 在同一个 AgentSession 和物理
Session 中继续这次收尾执行；收尾不替换会话，也不要求先 Reset。

同一 AgentSession 同时只执行一个由 Workflow 发起的输入；不同 AgentSession 可以并行。
用户在 Session 页面提交的 follow-up 是例外：Session 正在执行时加入当前执行，空闲时
开始新的执行。

### Session 操作

| 操作 | 结果 |
|---|---|
| Follow-up | 把用户文本交给当前物理 Session；确认后端已接收后返回 |
| Compact | 使用后端的原生压缩；Runtime Session 身份不变 |
| Reset | 在 Session 空闲时建立一个没有旧上下文的新物理 Session；AgentSession 保留已有会话内容 |

Compact 是用户在 Session 中发起的操作，不是 Workflow Action；Mohist 不生成假摘要
模拟压缩，压缩失败也不静默降级。Runner 重启后仍按 AgentSession 保存的绑定继续这些
操作。Compact 和针对执行中操作的命令不做缺失自动恢复；Reset 即使在旧 Session 已不
存在时仍可建立新的空 Session。

### 完成与失败

执行成功结束后，Workflow 才按 task 的 `expect`、`artifacts`、`failIf` 和 recovery
规则判断后续流程；执行失败、取消或超时时，原始错误就是 task 结果，不再检查文件或
marker。Action Output 只在命中 promise marker 时返回 `{ "promise": "..." }`，否则为
`null`；Session ID、模型、用量、完整文本与校验明细属于 Session 或任务状态，不塞进
Action Output。

执行期限从提交 Prompt 前开始计时，绑定与审计输入准备不占用该预算；收尾 Prompt 是
新的执行并获得新的期限。期限到达后中断当前执行并明确报告 timeout；中断后端只是
收尾，不能用缺少 marker 覆盖 timeout，也不替换当前 Session 绑定或自动 Reset。提交
结果不确定时不自动重放 Prompt，避免同一任务被执行两次。

provider 明确报告额度、余额或计费耗尽时，Mohist 中断当前执行并让本次 task 失败，
不等待 provider 继续重试；Session 绑定保持不变，回到空闲后可以选择其他模型继续，
无需 Reset。无法确认当前执行已经停止时，明确报告中断未确认，不把仍可能执行的
Session 显示为已经安全空闲。

### 共享错误码

两个执行类 Action 共享以下业务错误码，各篇只补充特有错误码：

| 错误码 | 含义 |
|---|---|
| `runtime-unavailable` | 后端执行能力尚未就绪或不可用 |
| `session-workspace-mismatch` | Session 绑定的工作目录与本次执行不一致 |
| `session-binding-failed` | 逻辑 Session 绑定的解析或持久化失败 |
| `runtime-session-missing` | 物理 Session 已不存在，但当前操作无法安全地自动重建或重新投递 |
| `unavailable-runtime` | 后端报告不可用 |
| `execution-failed` | 执行失败（含 provider 额度、余额或计费耗尽） |

## 实装差距

- `mohist/pi` 尚未实装,当前只有产品契约(见 [pi.md](pi.md) 的实装差距小节)。
- `mohist/agent` 尚未实装,当前只有产品契约(见 [agent.md](agent.md) 的实装差距小节)。
- Runner 派发时会按 manifest 校验未知字段、必填字段和类型;自定义 Profile 应在 `with`
  中显式绑定需要的 Variable 值。
