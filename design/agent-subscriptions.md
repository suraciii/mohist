---
purpose: "Agent 事件订阅：Agent 从「手动启动」升级为「监听 CloudEvent、按订阅响应提示词自动启动」。Agent 是 owner 的代理人，走与人相同的动作通道。订阅 = 过滤表达式 + 响应提示词 + 响应方式；互斥场景按优先级仲裁，非互斥场景允许 fan-out；Agent 自拉上下文。本文是技术方案；产品需求见 docs/agent-subscriptions.md。"
style:
  - "只记录已收敛的决策与理由；开放问题单列，标「(开放)」，不作决策。"
  - "中文为主，表格 + 少量代码/ASCII。"
include:
  - "领域边界：订阅归属 Agent 上下文，消费 CloudEvent PL，不构成反向模型依赖。"
  - "上下文获取：handler 只读信封自带字段，Agent 用 mo workflow get 自拉关联 issue；零反查、零盖印。"
  - "订阅模型：Filter 表达式（基于 CloudEvent 属性）+ 响应提示词 + 响应方式。"
  - "响应协调：互斥场景按优先级选一个 Agent 响应（兜底/接管）；非互斥场景允许 fan-out；可见性而非强制配置校验。"
  - "动作边界：Agent 是代理人，能做的动作也必须能由人做；Agent 不获得专属审批通道。"
  - "组件清单与落地顺序。"
exclude:
  - "grain 内部实现、EF mapping、SQL 细节。"
  - "响应提示词的内容设计（用户自配，见产品文档）。"
status: "WIP——技术方案。核心边界已收敛：①订阅归属 Agent 上下文、消费 CloudEvent PL；②订阅 = 过滤表达式 + 响应提示词 + 响应方式；③互斥响应按 Agent 优先级仲裁，非互斥响应允许 fan-out，可见性而非强制配置校验；④handler 只读信封、Agent 用 mo workflow get 自拉上下文；⑤前置依赖 mo workflow 命令套件已交付（issue #381）。开放项见末尾。"
---

# Agent 事件订阅（技术方案）

> 产品需求见 [`docs/agent-subscriptions.md`](../docs/agent-subscriptions.md)。本文只写技术方案。

## 动机与约束

产品要 Agent 能监听系统事件、按订阅自动响应。代理审批只是其中一个场景。技术上要同时满足三条硬约束：

- **Agent 是叶子域**（[`domain-analysis.md`](domain-analysis.md):98,105）：Agent 不反向依赖 Workflow / Issue 的领域模型。
- **CloudEvent 是 Published Language**（[`eventbus-v2.md`](eventbus-v2.md):110,166）：`[Subscription]` + `ICloudEventHandler` 是基础设施投递落点，不是业务域间的模型依赖。
- **Agent 是代理人**：Agent 能做的动作也必须能由人做。Agent 审批走 `mo workflow approve` / `mo issue approve`，不引入专属裁判通道。

## 领域边界（镜头 3）

### 订阅归属 Agent 上下文，消费 CloudEvent PL

`AgentSubscription` 是 Agent 域的新聚合，与 `Agent` 1:N。Agent 域从「执行者定义」扩展为「执行者定义 + 反应式订阅」。

**为什么合规**：Agent 订阅的对象是 **CloudEvent（PL 信封）**，不是 Workflow / Issue 的领域模型。CloudEvent 是事件总线提供的 PL 通道。Agent 依赖 PL（infra 层），不 `using Workflow.Domain` / `Issue.Domain`。「这个事件来自 issue 还是 workflow」是用户配置时（写过滤表达式）关心的，不是 Agent 运行时关心的。

### 上下文获取：handler 只读信封，Agent 自拉

**否决的方案**：
- **handler 反查 Issue/Workflow 读侧**（`IssueQuerier.GetIssueIdForWorkflowRunAsync` 等）——Agent 是叶子域，handler 若 `using Issue.Services` / `using Workflow.Infrastructure` 即反向依赖业务域。`IssueWorkflowCompletionHandler` 虽反查合法，但它是 **Issue 上下文**的代码消费 Workflow 事件推进自己，属合法的 Issue→Workflow 下游消费；Agent 是叶子，地位不同，不能照搬。
- **生产侧盖印进 CloudEvent subject/extensions**——让 workflow 事件携带 issue 身份会污染核心域，或引入 tricky 的 infra 抄录约定；且为 handler 其实不需要的东西引入复杂度。

**收敛的方案**：handler 只读 CloudEvent 信封自带的 `source`（runId）/ `data`（stage）/ `type`（事件类型），渲染进响应提示词。Agent 启动后用 `mo workflow get <runId> --json` 主动拉取 workflow 详情（含关联 issue 的 `IssueRef.Number + Title`，由 `WorkflowRunDetailDto` 提供，issue #381 已交付），再 `mo issue show <number>` 读 proposal 等。

```
收到 CloudEvent
  source = /mohist/workflow-runs/{runId}   ← 信封自带
  data   = { Stage: "plan" }               ← 信封自带
  ↓ handler 只读信封，不反查、不盖印、不跨域
渲染提示词（填 {{workflow_run_id}} {{stage}} {{event_type}}）
  ↓
Agent 启动，按提示词执行：
  mo workflow get <runId> --json   →  拿到关联 issue 号
  mo issue show <number>           →  读 proposal/tasks
  mo workflow approve/reject <runId>
```

**为什么合规**：handler 零业务域 `using`，纯 PL 消费，Agent 保持真叶子。workflow 事件、issue 事件零改动；eventbus-v2 的 identity 盖印与本功能解耦（盖印仍是 eventbus-v2 自己的优化项，不再是本功能前置）。

**前置依赖**：`mo workflow get` 返回关联 issue —— 已由 issue #381 交付（`WorkflowRunDetailDto.IssueRef`，server 端 `IssueQuerier.GetIssueRefForWorkflowRunAsync` 反向组装；CLI `mo workflow get`/别名 `show` 透传）。该反向组装发生在 API 读侧，不构成 workflow 域对 issue 域的模型依赖，与 `IssueWorkflowCompletionHandler` 同属合法的读侧组装。

## 订阅模型（核心）

一条 `AgentSubscription` 声明几件事 + 元数据：

```
AgentSubscription（Agent 域新聚合，1 Agent : N 订阅）
  Id, ProjectId, AgentId, Name,
  Filter,                     // 过滤表达式，见下
  ResponsePrompt (string),    // 带 {{workflow_run_id}} {{stage}} {{event_type}} 占位符
  CoordinationMode,            // fanout | exclusive（命名待定）
  Priority (int?),            // 可选；仲裁用，null 取默认
  Status (active|archived),
  timestamps
```

### Filter：过滤表达式（取代 EventTypes + Scope）

**收敛决策**：订阅不再分「事件类型 + 范围」两个字段，而是一个统一的过滤表达式。这取代了早期设计里的 `EventTypes[]` + `Scope`，对齐 CloudEvents Subscriptions API 的「订阅 = filter」模型。

表达式基于 CloudEvent 信封属性匹配，沿用现有 `[Subscription]` 机制已实现的语义（[`InMemoryEventBus.cs`](../packages/server/src/Mohist.Server/Infrastructure/Events/InMemoryEventBus.cs):95-109）并扩展到多属性：

| 现有能力（仅 type） | 扩展（多属性） |
|---|---|
| `\|` 多选（OR） | 对 type 仍适用 |
| `*` 全匹配 | 对 type 仍适用 |
| `prefix.*` 后缀通配（仅 `.*` 结尾） | 对 type 仍适用 |
| 精确匹配 | 扩展到 source/subject |

- **最简形式**（覆盖大多数场景）：一个 type 表达式，如 `com.mohist.workflow.stage.approval-requested` 或 `com.mohist.workflow.stage.*`。
- **精确到 issue**：用 source 属性约束，如同时匹配 `type=com.mohist.workflow.stage.approval-requested` 且 `source` 指向 issue #42 关联的 run。这取代了独立的 Scope 字段——「只盯某个 issue」就是表达式的一部分。

> **(开放)** 表达式的精确语法：是照搬 CloudEvents Subscriptions API 的 filter dialect（attribute-based，更通用），还是用现有 `|`+`.*` 扩展成简易多属性形式。后者实现轻、够用；前者更标准、更可移植。倾向先用简易扩展，待需求驱动再升级。匹配只在 CloudEvent 信封属性上发生，**零业务域查询**——这是守边界的硬约束。

### CoordinationMode + Priority（fan-out / 兜底 / 接管）

**产品约束**：不是所有事件都只能由一个 Agent 响应。

- 非互斥事件可以 fan-out：所有命中的 active 订阅都可触发。例：issue 完成后同时生成 release note、统计交付成本、通知 owner。
- 互斥事件需要仲裁：同一个审批点只应产生一个最终 approve / reject 决策。

**互斥机制**：每个 CloudEvent 实例到来时，按 Agent 仲裁，不是按订阅仲裁：

```
事件 E 到来
  ↓ 找出所有 Filter 命中 E 的 active 订阅
  ↓ 过滤出互斥响应订阅
  ↓ 按 Agent 归组：{AgentX: [sub1, sub2], AgentY: [sub3]}
  ↓ Agent 组之间按「组内最高订阅优先级」排序，最高优先级的 Agent 组赢
  ↓ 赢的 Agent 组内，按订阅优先级选一条订阅触发
  ↓ 触发：渲染该订阅的 ResponsePrompt → 调 IAgentLauncher → 给 session 打可见性标签
```

**为什么互斥时按 Agent 仲裁而非按订阅仲裁**：用户给同一 Agent 配多条订阅（不同提示词应对不同子场景）是正常配置，不该因同 Agent 内部命中多条被当成"多个 Agent 响应"而违反约束。同 Agent 内多条命中，组内按订阅优先级选一条。

**同优先级**：不报错、不拒绝、不阻塞。确定性选一个（实现细节，如订阅 id 字典序）。**核心防错机制是可见性，不是强制**（见下）。

> 注：互斥模式支持产品定义的「兜底 + 接管」模式——全局兜底订阅低优先级，特定 issue 订阅高优先级，事件来了高优先级 Agent 接管、低优先级不响应。

### 可见性（取代严格冲突检测）

**收敛决策**：不做配置时/运行时的严格冲突检测与拒绝。核心防错机制是**双向可见性**，用户据此核对配置是否符合预期。

每次订阅触发的 Agent session，打两个关联标签（落 AgentSession 现有 metadata label 机制，[`GenericAgentSessionMetadata.cs`](../packages/server/src/Mohist.Server/Sessions/Services/GenericAgentSessionMetadata.cs):36-65，新增 key）：

- `mohist.io/trigger/event-id` —— 触发它的 CloudEvent id
- `mohist.io/trigger/subscription-id` —— 命中的订阅 id

这样：
- **从事件查 Agent**：某次事件 → 被哪个 Agent、哪条订阅响应。
- **从 Agent job 查事件**：这个 Agent 这次执行 → 响应哪个事件、哪条订阅触发。

兜底/接管配错了（你以为 B 接管结果 A 跑了），从可见性发现，去调优先级。**配置正确性用户负责，可观测性系统负责。**

## 组件清单

### ① Subscription 聚合 + 存储

复刻 `InboxSubscription` 模式（最成熟先例：表 + Store + handler 查配置过滤）。聚合字段见上「订阅模型」。Store 提供 `ListByProjectAsync` / `ListByAgentAsync` / `GetAsync` / `SetAsync`。

### ② IAgentLauncher 内部 service

把现散在 `Api/AgentSessionLaunchRoutes.cs:73-97` 的 `mint sessionId → OpenAsync → 构造 AgentJobInput → SubmitAsync` 抽成 service。HTTP 层与新 handler 共用。纯提取，应顺手做。

### ③ 订阅分发 handler

新的 `[Subscription]` handler（归属 `Events.Subscriptions`，与 `InboxProjectionHandler` 同层），逻辑：

```
收到 CloudEvent
  ↓ 查 AgentSubscriptionStore：列出本 project 的 active 订阅
  ↓ Filter 匹配：用 CloudEvent 信封属性（type/source/subject）逐条对照订阅 Filter
  ↓ 按 CoordinationMode 分流：fanout 订阅全部触发；exclusive 订阅按 Agent 归组 + 优先级仲裁
  ↓ 渲染该订阅 ResponsePrompt（填 {{workflow_run_id}} {{stage}} {{event_type}}）
  ↓ 调 IAgentLauncher.LaunchAsync(agentId, renderedPrompt, contextRef, triggerLabels)
  ↓ triggerLabels 含 event-id + subscription-id，写进 session metadata
```

handler 零业务域 `using`，纯 PL 消费。骨架照搬 `InboxProjectionHandler`（查订阅配置 → 过滤 → 投递）。

### ④ 模板变量渲染（轻量）

约定**信封自带的三个变量**，简单字符串替换，不引入模板引擎：

| 变量 | 来源 | 说明 |
|---|---|---|
| `{{workflow_run_id}}` | CloudEvent `source`（`/mohist/workflow-runs/{runId}`） | Agent 用 `mo workflow get` 据此拉详情 |
| `{{stage}}` | CloudEvent `data.Stage` | workflow 事件 payload 自带 |
| `{{event_type}}` | CloudEvent `type` | 事件类型字符串 |

**不提供 `{{issue}}` 变量**——issue 号由 Agent 执行 `mo workflow get` 后从 `IssueRef.Number` 获取。issue 域事件如需订阅，其 `source` 是 `/mohist/issues/{id}`，handler 可解析出 issue 号直接渲染（同样零跨域）。

## (开放) 未定项

| # | 问题 | 倾向 | 理由 |
|---|---|---|---|
| 1 | Filter 精确语法 | 简易多属性扩展优先 | 实现轻、够用；标准 dialect 待需求驱动 |
| 2 | 动作可追溯 | MVP 不改 Workflow 裁判模型，记为审计缺口 | Workflow 不关心审批者是谁；审计视图需要从 session / API 调用侧追到发起者 |
| 3 | 配置入口 | Web UI 挂 Agent 详情页 Subscriptions 分区；CLI `mo agent subscribe` | 与 Agent CRUD 同址 |
| 4 | CoordinationMode 默认值 | 待定 | 审批类事件应 exclusive；通知、总结类事件可 fanout |

## 不做（守边界 / YAGNI）

- **不做** Agent 调 approve 的专属结构化通道——Agent 走 `mo workflow approve` / `mo issue approve`（同一 grain 方法），裁判权通道只该有一个。
- **不做** 严格冲突检测/拒绝——可见性取代强制（见上）。
- **不做** per-订阅重试 / outbox——复用事件总线现有投递 + AgentSession 失败可见。
- **不碰** workflow profile 的 `requiresApproval`——那是引擎自推进，与本功能正交。
- **不做** per-Agent 并发闸门（`MaxConcurrentRuns` 强制）——先用订阅响应方式和可见性控制范围；该字段后续按需评估。

## 落地顺序

```
0. (前置，已交付) mo workflow 命令套件 ✅ issue #381
   —— mo workflow get <runId> 返回 WorkflowRunDetailDto（含 IssueRef.Number+Title）
   —— 控制命令（approve/reject/retry/rerun/resume/pause/stop）按 runId 寻址
   ↓ 依赖已满足
1. IAgentLauncher 重构（纯提取，可独立验证）
2. Subscription 聚合 + Store + CRUD API
3. 分发 handler + Filter 匹配 + 响应方式 + 优先级仲裁 + 模板渲染（纯信封消费，零业务域依赖）
4. 可见性标签（session metadata trigger keys）+ EventCatalog 补登记 issue.* 事件 + Web/CLI 配置面
```

## 对应源码（动手前重读）

- Agent 实体与启动：`packages/server/src/Mohist.Server/Agent/`（`Domain/Agent.cs`、`Grains/AgentJobGrain.cs`、`Api/AgentSessionLaunchRoutes.cs`）
- CloudEvent 生产（无盖印，本功能不改）：`Infrastructure/Data/Workflow/WorkflowRunStore.cs:80-92`
- workflow 详情读模型（前置已交付）：`Api/WorkflowRunDetailDto.cs`（`Status + IssueRef`）、`Workflow/Services/WorkflowViews.cs:138-140`（`WorkflowRunIssueRef{Number,Title}`）、CLI `MohistCliCommands.Workflow.Reads.cs`
- 现有 Filter 匹配语义：`Infrastructure/Events/InMemoryEventBus.cs:95-109`（`\|`/`*`/`.*`）
- session metadata label 机制（可见性落点）：`Sessions/Services/GenericAgentSessionMetadata.cs:36-65`
- 事件订阅范式：`Events/Subscriptions/InboxProjectionHandler.cs`、`Inbox/InboxSubscriptionStore.cs`
- 边界依据：[`architecture.md`](architecture.md):49,99-108,254、[`domain-analysis.md`](domain-analysis.md):98,105,154、[`eventbus-v2.md`](eventbus-v2.md):110,166
