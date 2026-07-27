---
status: wip
---

# Agent 事件响应（Event Response）

事件路由决定「谁来响应」（[`event-routing.md`](event-routing.md)），本文定义
「响应本身遵守什么契约」：事件命中之后，从启动、行动到结果可见的全部保证。

## Model

响应不是新实体：一次响应 = 路由触发的一次 AgentJob（+ Agent launch 来源的
AgentSession）。响应的事实分三处，各有权威：

- AgentJob 裁定响应本身完成或失败；
- AgentSession 记录 agent 的行动过程；
- issue comment（若 agent 写了）承载对 owner 的交代。

## Semantics

### 响应保证

1. **至多一次**：每个 (eventId, ruleId) 至多启动一次，幂等键由启动管线保证。
2. **基于当前状态，不是事件快照**：事件只说「发生过什么」。agent 行动前必须用
   命令面核对「现在是什么」（例如 approve 前确认 run 仍在等审批）。领域命令对
   过期状态明确失败——approve 一个不在等审批的 run 会被拒绝——agent 必须把
   拒绝当作正常信号处理，而不是当成自己的错误重试。
3. **无串行化**：同一 issue 的多个响应可以并发（路由触发与 @ 提及、连续事件）。
   不建 per-issue 锁：冲突由目标聚合的命令校验拒绝，不产生脏状态。真实使用
   出现冲突困扰时再评估。
4. **响应失败必须可见**：AgentJob 终态失败（含 preflight 失败）发射
   `com.mohist.agent.job.failed`，stamping 含 `agentid` 与业务谱系（issue /
   epic / workflowrunid，如有）。该事件默认进入 inbox 与 hermes（新通知种类
   「Agent 响应失败」，默认开）——「owner 以为 agent 在处理，其实没有」不能
   静默。
5. **失败事件可路由，但防自响应**：`agent.job.failed` 与其它事件同权进入路由
   协议；规则的 AgentId 与信封 `agentid` 相同时视同不命中（envelope-only 检查，
   记结构化日志）。两个 Agent 互相响应对方失败（A→B→A）的循环无法由此斩断，
   属用户配置责任，靠干跑与可见性发现。

### 可归属

agent 的每个决定必须与人的可区分，这是 owner 回看历史能接手的前提：

- comment：`--author` 声明 agent 自己的名字（约定，已写入监管预设文本）。
- 审批决议：可附带声明式操作者 `decidedBy`，与 comment author 一样是声明而非
  认证。`mo run approve` / `mo run reject` 可用 `--author` 署名；审批决议事件与
  读取模型在有署名时携带该字段。Agent 应主动署名，人操作时可以省略。
- Web UI 的人工 approve / send back 不要求填写操作者。未署名决议保持
  `decidedBy` 为空，不以 `web`、`owner` 或其它合成值代替。

### Not doing

- per-issue 响应串行锁。
- 响应自动重试：job 失败靠 `agent.job.failed` 上浮；重试等于新事件或人工动作。
- 触发频控 / 冷却期：沿用 [`event-routing.md`](event-routing.md) 的 Not doing。
- 被监管事件的直达通知抑制：通知语义见
  [`agent-supervision.md`](agent-supervision.md) 的升级模型。

## Status

`agent.job.failed` 事件与通知种类、可选审批 `decidedBy` 及 Agent 的操作者声明
均已实装。响应保证 1–3 描述的是启动管线与领域命令的既有行为，本文把它们
固定为契约。
