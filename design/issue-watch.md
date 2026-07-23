---
status: wip
---

# Issue 关注（Watch）

Issue 级的 Agent 关注声明：被关注的 issue 到达审批门或终态失败时，Agent 自动
响应。这是 issue 的 autopilot 开关——路由表管项目级与任意表达式
（[`event-routing.md`](event-routing.md)），关注管「这一个 issue」的日常开关；
响应本身遵守 [`event-response.md`](event-response.md) 的契约。

## Model

```text
WatchEntry
  ProjectId, IssueNumber, AgentId
  State: watching | muted
```

- WatchEntry 由 Agent context 拥有与持久化。Issue 聚合不持有它；issue 详情里的
  「关注 / 静音」两块是对 WatchEntry 的读取投影。
- `watching`：该 issue 的审批请求与 run 终态失败事件启动该 Agent。
- `muted`：压制该 Agent 在该 issue 上的一切启动，包括路由规则命中。
- 事件集固定为 `stage.approval-requested` 与 `run.failed`——autopilot 的含义
  就是这两类时刻，不可配置；要花式响应用路由规则。
- 关注启动使用内置响应提示词（事件事实 + 「按你的身份指令处理」），没有
  per-rule ResponsePrompt；纪律由 Agent 的身份指令承载。

## Semantics

### 命令面

```text
mo issue watch add <issue> --agent <name>
  无声明        -> 建 watching
  已 muted      -> 转 watching
  已 watching   -> 幂等，报告现状

mo issue watch remove <issue> --agent <name>
  已 watching   -> 删除声明
  无声明        -> 建 muted（语义：该 Agent 被项目级规则覆盖，这里撤回）
  已 muted      -> 幂等，报告现状

mo issue watch list <issue>
  列出该 issue 的 watching 与 muted；`mo issue view` 展示同样两块。
```

`watch add` / `remove` 校验 Agent 存在且 active；对已 archived 的 Agent 拒绝。

### 启动

带 `issue` 属性的事件到达后，分发侧在路由表求值之外查 WatchEntry：

```text
for 命中规则的 agent:           # 路由路径，不变
  if (issue, agent) 是 muted: 视同不命中，记结构化日志

if 事件类型 ∈ {approval-requested, run.failed}:
  for (issue, agent) 是 watching 的声明:
    launch(agent, prompt = 内置模板, context = issue)
```

- 幂等键按 `hash(projectId, eventId, agentId)` 归一：同一事件里同一个 Agent
  无论被规则命中还是被关注命中，只启动一次。
- muted 的压制先于一切启动发生；同一 issue 上 muted 优先于任何规则与关注
  （watching 与 muted 不可能同时存在，状态唯一）。
- workspace 解析、触发标签、preflight 失败进失败 AgentJob 等行为与路由启动
  一致；触发标签注明来源是 watch。

### 与路由表的分工

- 项目级监管、任意事件类型、任意匹配表达式 → 路由规则，有排序与 Continue。
- 单 issue 的日常开关 → 关注；不进入路由表，没有排序语义。
- `@` 提及要求持续关注时，Agent 用 `mo issue watch add` 兑现，不再手写路由
  规则（见 [`agent-mentions.md`](agent-mentions.md)）。

## Status

全部未实装。实施 issue 待创建。依赖路由启动管线（workspace 解析、幂等键、
触发标签均复用）；读取投影进 `mo issue view` 与 Web issue 详情。
