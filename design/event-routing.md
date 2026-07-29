---
status: converged
---

# Agent 事件路由（Routing Table）

Mohist Agent 通过项目级**事件路由表**自动响应系统事件，取代手动 launch。
本文的 Agent 均指 Mohist Agent（术语与所有权不变量见
[`agent-execution.md`](agent-execution.md)）；信封协议与匹配表达式语法见
[`event-protocol.md`](event-protocol.md)。

本设计已取代早期「订阅 + 优先级仲裁」模型（AgentSubscription + Arbitrate）；
旧模型迁移见文末。

## 边界

路由属于 Agent context。消费 CloudEvent PL（infra 层）。禁止 `using
Workflow.Domain` 或 `Issue.Domain`。匹配与渲染 envelope-only，零跨域反查。

## 模型

```
RoutingRule（项目级，有序表）
  Id, ProjectId, Name
  Position                  表内序号，唯一；求值按此序
  Match                     匹配表达式（event-protocol.md 定义的 CEL 子集）
  AgentId                   响应 Agent
  ResponsePrompt            模板，{{event.<attr>}} 占位符
  Continue                  bool；命中后是否继续向下求值（默认 false）
  Status                    active | archived
```

一个项目一张表；规则引用 Agent，Agent 不拥有规则（早期「1 Agent : N 订阅」的
归属关系取消——规则的排序语义是表级的，挂在 Agent 下无法表达跨 Agent 的
兜底/接管次序）。

## 求值语义

路由表是项目级的：信封无 `projectid` 的事件不进入任何路由表（与现有分发行为
一致）。事件到达（带 `projectid`）→ 取该项目 active 规则按 `Position` 升序：

1. 逐条求值 `Match`；不命中 → 下一条。
2. 命中 → 渲染 `ResponsePrompt`，经 `IAgentLauncher` 启动 Agent；
   `Continue == false` → 求值结束；`true` → 继续下一条。
3. 命中但不可执行（Agent 已 archived、渲染后 prompt 为空）→ 视同不命中，
   记结构化日志，继续下一条。
4. 表达式运行期异常 → 视同不命中（见 event-protocol.md）。
5. 同一事件里同一 Agent 至多启动一次：它已被前序规则或关注声明启动时，
   后续命中它的规则不再启动，记结构化日志；响应提示词取自首个启动它的
   规则。

由此得到：

- **exclusive（默认）**：first-match-wins，排序即优先级，无数字算术；
- **fanout**：上游规则标 `Continue`；
- **兜底 + 接管**：针对性规则排在兜底规则上方。

无 Arbitrate、无 Priority 字段、无 CoordinationMode。

## 写入时校验

创建/更新规则时：

- `Match` 编译失败 → 拒绝；
- `AgentId` 不存在或非 active → 拒绝；
- `ResponsePrompt` 空 → 拒绝。

运行期只做求值，不做校验兜底（Agent 事后 archived 属运行期跳过情形）。

## 渲染

`{{event.<attr>}}` 直接替换信封属性（与 Match 同一命名空间），envelope-only、
无模板引擎、未命中占位符原样保留。旧 token（`{{workflow_run_id}}`、`{{stage}}`、
`{{event_type}}`）保留为别名。`{{event.stage}}` 依赖 stage 提升为信封属性
（stamping 矩阵 workflow 族），不再从 `data` 解析。

## 幂等与可见性

- Launcher key = `hash(projectId, eventId, agentId)`：同一事件里同一 Agent
  至多启动一次，无论命中它的是哪条规则或关注声明；重复分发不会重复起
  job（沿用 AgentLauncher 幂等启动机制）。命中规则只作触发归因（trigger
  标签），不进幂等键。
- 触发的 AgentSession 打标签：`mohist.io/trigger/event-id`、
  `mohist.io/trigger/rule-id`。事件 → 规则 → AgentJob 双向可查。
- AgentJob 裁定响应完成；AgentSession 以 SessionInput、AgentTurn 和 transcript 提供对话与
  审计证据。

## 与系统 handler 的关系

路由表是用户态消费面；`[Subscription]` handler 是系统态消费面。两者消费同一
信封协议，经同一个分发器投递。Agent 无特殊通道：响应动作走
`mo workflow approve` / `mo issue comment` 等正规命令面。

## 命令面

```
mo routing rule create --name <n> --match <expr> --agent <agent> \
    --response-prompt <p> [--continue] [--before <rule> | --after <rule>]
mo routing rule list | view <n> | edit <n> | archive <n>
mo routing rule move <n> --before <rule> | --after <rule>
mo routing test [--limit <N>]    # 用最近 N 个事件干跑整张表，逐条显示命中
mo event tail [--match <expr>]   # 用同一 matcher 过滤事件流
```

命名遵循 [`cli.md`](cli.md)：资源在前、项目作用域走 active project / `--project`。

## 迁移

迁移不做数据自动转换：旧模型（`AgentSubscription` + `Arbitrate`）与
`mo agent subscription` 命令面已直接删除，不留兼容层（项目积极开发期，无
版本兼容义务）。旧订阅由操作者按规则手工重配（Filter 三字段可机械对应
表达式：`event.type == "..." && event.source == "..."`，Priority 降序对应
表内顺序）。

## Not doing

- Agent 专用审批通道——走正规命令面。
- 严格冲突检测——干跑 + 可见性替代。
- 规则级 retry/outbox——复用分发器投递保障 + AgentJob 失败可见性。
- `event.data.*` 匹配——按 event-protocol.md 准入标准提升属性。
- 每 Agent 并发闸——先靠规则与可见性控制。
- 触发频控 / 冷却期——监管型 Agent 的循环风险（失败→rerun→失败→再触发）
  短期由响应提示词自限（如 comment 计数），系统级频控留待真实需求出现。

## Status

已实装：项目级有序路由表（`Position` / `Continue`）、CEL 子集表达式匹配与
写入时编译、`{{event.*}}` 渲染、envelope-only 自响应防护、`mo routing rule`
命令面与 `mo routing test` 干跑、`mo event tail --match`；旧订阅模型及其
命令面已删除（`DropAgentSubscriptions` 迁移）。

实装差距：启动管线耐久键仍按 `(projectId, eventId, ruleId)`，(event, agent)
合并只在单次分发内生效；归一到 agentId 键由 issue #532 收敛。
