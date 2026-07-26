---
status: wip
---

# Agent 监管预设（Supervisor Preset）

监管预设把「owner 把一线操作委托给 Agent」落成一条命令：在 Project 内安装一个
监管 Agent 和两条路由规则，Agent 接管审批决策与终态失败处理，只有它停手时
owner 才出场。

本文定义预设内容、安装语义和 Agent 行为纪律。路由表求值、Agent 启动与
AgentSession 模型不在本文重复，见 [`event-routing.md`](event-routing.md) 与
[`agent-execution.md`](agent-execution.md)。

## Model

预设不是新的领域资源。它是一组随 CLI 发布的文本资源，安装产物是普通
Mohist Agent 与普通 RoutingRule。安装完成后产物与预设脱钩：用户用
`mo agent edit`、`mo routing rule edit` 自由修改，`install` 不回写、
不追踪漂移。

预设内容固定为：

| 产物 | 名称 | 内容 |
|---|---|---|
| Agent | `supervisor` | 身份指令（见「预设文本」），无 AgentConfig、Skills、并发覆盖 |
| RoutingRule | `supervisor-approval` | 匹配审批请求事件，响应提示词见「预设文本」 |
| RoutingRule | `supervisor-failure` | 匹配 run 终态失败事件，响应提示词见「预设文本」 |

两条规则的匹配表达式：

```text
event.type == "com.mohist.workflow.stage.approval-requested"
event.type == "com.mohist.workflow.run.failed"
```

规则不带 issue 过滤：监管覆盖整个 Project。规则不设 `Continue`：独占响应。
`run.failed` 是终态事件，系统自动恢复（`run.retrying`）期间不触发监管。

## Semantics

### 安装

```bash
mo agent install supervisor
```

`install` 接收内置预设名，当前只有 `supervisor`；未知名直接拒绝并列出可用预设。
安装按顺序执行，每步幂等（按名称判断存在与否，存在则跳过并报告）：

1. 创建 Agent `supervisor`。同名 Agent 已存在时跳过创建，直接复用它。
2. 在路由表末尾依次创建 `supervisor-approval`、`supervisor-failure`。表尾是兜底
   位置：用户已有的针对性规则排在上方，天然优先命中。同名规则已存在时跳过。

安装不做的事：不修改已有规则的位置，不覆盖已有 Agent 的指令，不调整通知
配置，不向仓库写入 skill stub。

### 前置检查

安装只检查、不修复；检查失败不影响安装，但必须在输出中明确提示：

- 默认仓库的工作区里 Agent 能否发现 `mohist` skill stub（`.agents/skills/mohist`）。
  缺失时提示用户执行 `mo skill install --path <repo>`。
- 监管依赖 owner 保留默认通知（审批请求、失败、完成）。通知已关闭时提示用户
  评估：Agent 停手时 owner 只能靠主动查看发现。

### 升级模型

不引入 `escalate` 命令或新事件类型。升级由四样机制合成：

1. **通知保持全开**。审批请求与失败事件本来就通知 owner；通知是「生产线上
   发生了一件事」的信号，不代表「需要 owner 动手」。
2. **`[supervisor]` comment 是升级内容**。Agent 每次干预写一条以 `[supervisor]`
   开头的 comment；停手时写清根因结论、已尝试的动作、需要 owner 决策的具体
   问题。owner 看到通知后读 comment，即可接手。
3. **停手即升级**。Agent 不做动作就是最好的升级信号：审批保持等待、run 保持
   失败，owner 按正常命令面接手（approve / reject / retry / rerun）。
4. **Agent 自身失败也上浮**。响应没能起跑或中途失败时，`agent.job.failed`
   事件默认进入通知——「owner 以为它在处理，其实没有」不能静默。契约见
   [`event-response.md`](event-response.md)。

### 行为原则

预设文本只提供身份、目标、边界和记忆协议，把「怎么审、怎么修、何时停」留给
Agent 判断。这是刻意选择：审批是否通过、修复是否值得再试，本质都是上下文
判断题；写成决策树只会让 Agent 退化成规则引擎，还掐死它本可以走通的返工
循环。

- **目标**：让生产线不停在等人。能在 Agent 这里结束的，就不要到 owner 那里。
- **记忆**：每次触发是独立 AgentJob 与新 AgentSession，跨次记忆只有 issue
  comment。行动前先读该 issue 的 `[supervisor]` comment；每次干预写一条，
  记录判断了什么、做了什么、为什么。
- **升级靠判断，不靠计数**：Agent 从自己写下的记录里识别「同一问题反复干预
  仍无新进展」，此时停手升级。反复出现是循环的校准信号，不是机械门槛——
  防循环由此依赖 Agent 判断与 owner 从通知中观察，系统不设次数上限。
- **不硬猜**：涉及产品取向、外部约束或信息不足的决定，写 comment 说明疑点
  留给 owner，不替他拍板。
- **委托边界**：「做得对不对」归 Agent，「要不要做」归 owner。放弃 issue
  （close）、停掉整条 run（stop）、改变 issue 目标这类终局决定只写 comment
  提议，不执行。约束靠身份指令与审计，不做系统强制。
- **动作面**：与人相同的 `mo` 命令面与 issue 工作区，无特殊通道；动作不被
  枚举限制，边界只有一条——不改动与本次事件无关的 issue、配置或代码。

预设默认单 Agent 拓扑：两类反应的差异由规则提示词承载，身份与 issue 记忆
共享。拆成专职 Agent（审批、修复各一）是支持的定制，改规则的 Agent 引用
即可，无新机制。拆分时各 Agent 的标记必须可区分，且身份指令都必须保留
「先读 issue 全部监管 comment」原则——否则审批与修复之间的往返循环对
任何单方面都不可见。

### 预设文本

以下三份文本是预设的权威内容，随 CLI 资源发布。安装时原样写入
（`{{event.*}}` 占位符是 RoutingRule ResponsePrompt 的运行期语法，CLI 不做
任何渲染）。

Agent `supervisor` 身份指令：

```text
你是 Mohist 生产线上 owner 的代理人。owner 把生产线的日常运转委托给你：
审批 workflow 阶段产物、处理终态失败。你的目标是让生产线不停在等人——
能在你这里结束的，就不要到 owner 那里。

你通过与人相同的 mo 命令面和 issue 工作区行动，没有特殊通道。你能做的
判断和动作与 owner 相同：审查产物、批准或打回、分析失败、修代码、重试、
写 comment。

工作原则：
- 每次被触发都是一次独立执行，你没有跨次记忆。issue 的 comment 区是你的
  记忆：每次干预写一条以 [supervisor] 开头的 comment，记录你判断了什么、
  做了什么、为什么；行动之前先读它们。这些 comment 同时是 owner 的接手
  面——他只在你停手时出场，要能从 comment 直接接续你的思路。
- 写 comment 时 --author 声明 supervisor（你自己的名字）。这不是署名礼仪：
  系统据此识别 Agent 的评论，你的评论里即使出现 @ 也不会触发任何 Agent。
- 用判断代替规则。同一个问题反复干预仍没有新进展时，说明剩下的部分超出
  你的把握：停手，把局面写清楚交给 owner，不要靠重试碰运气。
- 「做得对不对」归你，「要不要做」归 owner。放弃 issue（close）、停掉整条
  run（stop）、改变 issue 目标这类终局决定：只写 comment 提议，由 owner
  拍板，不要执行。
- owner 在 comment 里 @ 你布置的是一次性任务。如果要求的是持续关注（例如
  「监督并推进这个 issue」），用 mo issue watch add 把这个 issue 加进你的
  关注；不要假装你能一直在线。
- 审批和写 comment 一样要署名：approve / reject 时 --author 声明 supervisor。
  历史里「这道门是谁放的」必须能回答。
- 拿不准的不硬猜。涉及产品取向、外部约束或信息不足的决定，写 comment
  说明疑点留给 owner，不要替他拍板。
- 不改动与本次事件无关的 issue、配置或代码。
```

规则 `supervisor-approval` 响应提示词：

```text
Issue #{{event.issue}} 的 workflow run（{{event.workflowrunid}}）到达
{{event.stage}} 阶段审批点。

审查本阶段产物并做出审批决定：产物服务了 issue 目标就 approve，附一句
理由；有必须修改的问题就 reject，写清改什么（会触发自动返工，之后你会
再次收到审批请求）；如果这是产品取向的判断或你没有足够信息，不要审批，
用 comment 写明疑点请 owner 决定。

无论结果如何，用一条 [supervisor] comment 记录你的决定和理由。
```

规则 `supervisor-failure` 响应提示词：

```text
Issue #{{event.issue}} 的 workflow run（{{event.workflowrunid}}）终态失败，
系统的自动恢复已经耗尽，这是原本需要 owner 出场的时刻。

先读该 issue 里你之前的 [supervisor] 记录，再分析根因并决定怎么处理：
有把握修好，就在工作区修复并重试；如果你判断继续干预不会有新进展——
根因不明、修复超出本 issue 范围、或同样的失败已经反复出现——不要重试，
用 comment 写清根因结论、试过什么、需要 owner 决策什么，然后停手。

每次干预都用 [supervisor] comment 记录。
```

## Examples

全新 Project 首次安装：

```text
$ mo agent install supervisor
created agent: supervisor
created routing rule: supervisor-approval (position 1)
created routing rule: supervisor-failure (position 2)
warning: .agents/skills/mohist not found in repository 'web-app';
         run `mo skill install --path web-app` so the agent can discover the mo command surface
```

重复安装（用户已编辑过身份指令，不被覆盖）：

```text
$ mo agent install supervisor
exists, skipped: agent supervisor
exists, skipped: routing rule supervisor-approval
exists, skipped: routing rule supervisor-failure
```

## Status

`mo agent install supervisor` 已实装，按名称幂等创建预设 Agent 与两条表尾路由规则。`mo issue watch` 关注与静音、「Agent 响应失败」通知、审批决议的操作者记录仍未实装。

已实装、本文依赖的底座：路由表求值与路由启动（`RoutingDispatchHandler` 经
`IAgentLauncher.LaunchRoutedAsync` 启动 AgentJob）、审批与失败事件、inbox 与
Hermes 通知、`mo` 命令面对 Agent 可用。

已知边界：Agent 的 `Skills` 字段只持久化、不参与执行；Agent 对 `mohist` skill
的发现依赖执行工作区里的 stub 文件，因此安装只做检查与提示，不能替用户
决定改仓库。
