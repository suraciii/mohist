---
purpose: "Agent 事件订阅：Agent 从「手动启动」升级为「监听 CloudEvent、按订阅响应提示词自动启动」。订阅 CloudEvent（PL 信封），不订阅任何业务域模型。本文是技术方案；产品需求见 docs/agent-subscriptions.md。"
style:
  - "只记录已收敛的决策与理由；开放问题单列，标「(开放)」，不作决策。"
  - "中文为主，表格 + 少量代码/ASCII。"
include:
  - "领域边界判定：订阅归属 Agent 上下文，消费 CloudEvent PL，不构成反向模型依赖。"
  - "上下文获取：handler 只读信封自带字段（runId/stage），Agent 用 mo workflow get 自拉关联 issue；零反查、零盖印、零业务域依赖。"
  - "组件清单：Subscription 聚合 / IAgentLauncher / 分发 handler / 模板渲染。"
  - "落地依赖与顺序。"
exclude:
  - "grain 内部实现、EF mapping、SQL 细节。"
  - "响应提示词的内容设计（用户自配，见产品文档）。"
status: "WIP——技术方案。核心边界已收敛：①订阅归属 Agent 上下文、消费 CloudEvent PL；②handler 只读信封、Agent 用 mo workflow get 自拉上下文、零反查零盖印；③前置依赖 mo workflow 命令套件（独立功能）。开放项见末尾表格。"
---

# Agent 事件订阅（技术方案）

> 产品需求见 [`docs/agent-subscriptions.md`](../docs/agent-subscriptions.md)。本文只写技术方案。

## 动机与约束

产品要 Agent 能监听事件、按订阅自动响应。技术上要同时满足两条硬约束：

- **Agent 是叶子域**（[`domain-analysis.md`](domain-analysis.md):98,105）：Agent 不反向依赖 Workflow / Issue 的领域模型。
- **CloudEvent 是 Published Language**（[`eventbus-v2.md`](eventbus-v2.md):110,166）：`[Subscription]` + `ICloudEventHandler` 是基础设施投递落点，不是业务域间的模型依赖。

## 已收敛决策

### 决策 1：Subscription 属于 Agent 上下文

`AgentSubscription` 是 Agent 域的新聚合，与 `Agent` 1:N。Agent 域从「执行者定义」扩展为「执行者定义 + 反应式订阅」。

**为什么合规**：Agent 订阅的对象是 **CloudEvent（PL 信封）**，不是 Workflow / Issue 的领域模型。CloudEvent 是事件总线提供的 PL 通道。Agent 依赖 PL（infra 层），不 `using Workflow.Domain` / `Issue.Domain`。

「这个事件来自 issue 还是 workflow」是**用户配置时**关心的（选 EventTypes），不是 Agent 运行时关心的。Agent 运行时只看到一个 CloudEvent 信封。

**否决的备选**：
- 独立「AgentReactions」横切上下文——过度设计。订阅的 1:N 归属天然在 Agent 上，消费 PL 不构成模型依赖，没有理由把它从 Agent 域拆出去。
- 复用 Inbox 上下文——Inbox 语义是「呈现给人」，Agent 订阅语义是「触发执行」，强塞会让 Inbox 反向依赖 Agent，且语义混淆。

### 决策 2：Agent 自拉上下文，handler 不反查、事件不盖印

Agent 订阅 handler 启动 Agent 时，响应提示词需要上下文（这是哪个 workflow run、什么 stage、关联哪个 issue）。这些值从哪来？

**否决的方案**：

- **handler 反查 Issue/Workflow 读侧**——`IssueQuerier.GetIssueIdForWorkflowRunAsync` 等。违规：Agent 是叶子域，handler 若 `using Issue.Services` / `using Workflow.Infrastructure` 即反向依赖业务域。（注：`IssueWorkflowCompletionHandler` 虽反查合法，但它是 **Issue 上下文**的代码消费 Workflow 事件推进自己，属合法的 Issue→Workflow 下游消费；Agent 是叶子，地位不同，不能照搬。）
- **生产侧盖印进 CloudEvent subject/extensions**——让 workflow 事件携带 issue 身份。否决理由：把"找 issue"的责任塞进事件信封，要么 workflow 主动理解 issue（污染核心域），要么引入一个 infra 盖印层机械抄录 annotations（tricky 的隐式约定）。两路都为 handler 其实不需要的东西引入复杂度。

**收敛的方案：handler 只渲染信封自带字段，Agent 用 `mo workflow get` 主动拉取 workflow 详情（含关联 issue）。**

关键洞察：issue 身份是**响应提示词的需求**，不是 handler 的硬需求。用户写订阅提示词时不知道会触发哪个 issue，提示词天然是泛化的。而 workflow run id 是 CloudEvent `source` 自带的，stage 是 `data` 自带的——handler 只渲染这两个，就够了：

```
收到 CloudEvent（如 approval-requested）
  source = /mohist/workflow-runs/{runId}   ← 信封自带
  data   = { Stage: "plan" }               ← 信封自带
  ↓ handler 只读信封，不反查、不盖印、不跨域
渲染提示词：
  "workflow run {{workflow_run_id}} 在 {{stage}} 阶段进入审批门。
   `mo workflow get {{workflow_run_id}} --json` 拉取它的详情（含关联 issue），
   再 `mo issue show <number>` 读 proposal，approve 或 reject。"
  ↓
Agent 启动，按提示词执行：
  mo workflow get <runId> --json   →  拿到 issue number、stage、状态等
  mo issue show <number>           →  读 proposal/tasks
  mo issue approve/reject <number>
```

**为什么合规**：
- handler 只读 CloudEvent 信封（PL），不 `using` 任何业务域。Agent 保持真叶子。
- workflow 事件零改动，issue 事件零改动，eventbus-v2 的 identity 盖印与本功能解耦（盖印仍是 eventbus-v2 自己的优化项，但不再是本功能的前置依赖）。
- 「找关联 issue」交给 Agent（智能体）和用户提示词，不交给 handler（机械代码）——契合产品定位「判断逻辑写在提示词里，不写在程序里」。

**前置依赖**：Agent 要能 `mo workflow get <runId>` 拿到关联 issue。当前 `mo` 没有 workflow 命令套件，需**先做**（见决策 3）。

### 决策 3：前置需求——`mo workflow` 命令套件

Workflow 是产品一等公民，应有独立的用户面命令套件，而不是只能通过 `mo issue` 间接操作。这是 Agent 订阅的前置需求，也是独立的产品价值。

**MVP 范围（支撑 Agent 订阅）**：`mo workflow get <runId> --json`——返回一个 workflow run 的详情读模型，**至少包含**：run id、状态、当前 stage、各 stage 进展、审批态、**关联 issue（number + 标题）**。

读模型来源已有基础：`WorkflowActiveWorkView`（`IWorkflowGrain.cs:122-130`）和 `WorkflowFeedbackRecord`（`:140-148`）已经在显式字段层面携带 `IssueId` / `IssueNumber`——「workflow 读模型带 issue 号供 correlate」是已确立模式。新建的 workflow 详情读模型延续这一模式。

**后续范围（独立演进，不阻塞 Agent 订阅）**：`mo workflow list`、把现散在 `mo issue` 下的 workflow 操作（approve/retry/rerun/rerun-from-stage/stop）归拢到 `mo workflow` 子命令组等。详见 `mo workflow` 套件自己的设计文档（待建）。

> 注：`mo workflow` 套件是**独立功能**，单独立项，不在本设计文档展开。本设计文档只声明对它的依赖（`mo workflow get` 返回关联 issue）。

## 组件清单（归属 Agent 上下文）

### ① Subscription 聚合 + 存储

复刻 `InboxSubscription` 模式（最成熟先例：表 + Store + handler 查配置过滤）。

```
AgentSubscription（Agent 域新聚合）
  Id, ProjectId, AgentId, Name,
  EventTypes (string[]),      // com.mohist.* 清单
  Scope,                      // 见「(开放)」
  ResponsePrompt (string),    // 带 {{issue}} {{stage}} 占位符
  Status (active|archived),
  timestamps
```

### ② IAgentLauncher 内部 service

把现散在 `Api/AgentSessionLaunchRoutes.cs:73-97` 的 `mint sessionId → OpenAsync → 构造 AgentJobInput → SubmitAsync` 抽成 service。HTTP 层与新 handler 共用。纯提取，应顺手做。

### ③ 订阅分发 handler

新的 `[Subscription]` handler，逻辑：

```
收到 CloudEvent
  ↓ 只读信封：workflow_run_id (source) + stage (data) + event_type (type)
  ↓ 查 AgentSubscriptionStore：按 EventType + Scope + Status=active 匹配
  ↓ 对每条命中：渲染 ResponsePrompt（填 {{workflow_run_id}} {{stage}} {{event_type}}）
  ↓ 调 IAgentLauncher.LaunchAsync(agentId, renderedPrompt, contextRef)
```

骨架照搬 `Events/Subscriptions/InboxProjectionHandler.cs`（查订阅配置 → 过滤 → 投递）。handler 零业务域 `using`，纯 PL 消费。

### ④ 模板变量渲染（轻量）

约定**信封自带的三个变量**：

| 变量 | 来源 | 说明 |
|---|---|---|
| `{{workflow_run_id}}` | CloudEvent `source`（`/mohist/workflow-runs/{runId}`） | 定位线索；Agent 用 `mo workflow get` 据此拉详情 |
| `{{stage}}` | CloudEvent `data.Stage` | workflow 事件 payload 自带 |
| `{{event_type}}` | CloudEvent `type` | 事件类型字符串 |

简单字符串替换，不引入模板引擎。**不提供 `{{issue}}` 变量**——issue 号由 Agent 执行 `mo workflow get` 后从返回里获取（见决策 2）。issue 域事件如需订阅，其 `source` 是 `/mohist/issues/{id}`，handler 可解析出 issue 号直接渲染（同样零跨域）。

## (开放) 其余未定项

| # | 问题 | 倾向 | 理由 |
|---|---|---|---|
| 1 | Scope 粒度 | 项目内所有 + 特定 issue 列表 | 覆盖最常用场景；epic 级先不做（YAGNI） |
| 2 | 并发控制 | 强制启用 `Agent.MaxConcurrentRuns`（per-Agent 闸门） | 该字段已建模未强制；1:N 扇出后变必需 |
| 3 | 审批可追溯 | MVP 不做，记为已知缺口 | 改 ApprovalStatus 牵动核心域，先靠 prompt 让 Agent 自述理由 |
| 4 | 可观测性 | 复用 AgentSession 查询 + 给触发 session 打 `source=agent-subscription` label | 不新建通道 |
| 5 | 配置入口 | Web UI 挂 Agent 详情页 Subscriptions 分区；CLI `mo agent subscribe` | 与 Agent CRUD 同址 |

## 不做（守边界 / YAGNI）

- **不做** Agent 调 approve 的专属结构化通道——Agent 走 `mo issue approve` / API 侧信道，裁判权通道只该有一个。
- **不做** per-订阅重试 / outbox——复用 eventbus-v2 的 at-least-once + AgentSession 失败可见。
- **不碰** workflow profile 的 `requiresApproval`——那是引擎自推进，与本功能正交。

## 落地顺序

```
0. (前置，独立功能) mo workflow 命令套件：至少 mo workflow get <runId> --json
   —— 返回 workflow 详情读模型，含关联 issue（number + 标题）
   ↓ 依赖
1. IAgentLauncher 重构（纯提取，可独立验证）
2. Subscription 聚合 + Store + CRUD API
3. 分发 handler + 模板渲染（纯信封消费，零业务域依赖）
4. MaxConcurrentRuns 强制启用
5. EventCatalog 补登记 issue.* 事件 + Web/CLI 配置面
```

## 对应源码（动手前重读）

- Agent 实体与启动：`packages/server/src/Mohist.Server/Agent/`（`Domain/Agent.cs`、`Grains/AgentJobGrain.cs`、`Api/AgentSessionLaunchRoutes.cs`）
- CloudEvent 生产：`Infrastructure/Data/Workflow/WorkflowRunStore.cs:80-92`（ToCloudEvent，当前无盖印）
- WorkflowRun 身份来源：`Workflow/Grains/WorkflowGrain.cs:113-114, 893-912`（Annotations 暴露为 ProjectId/IssueId/IssueNumber）
- 事件订阅范式：`Events/Subscriptions/InboxProjectionHandler.cs`、`Inbox/InboxSubscriptionStore.cs`
- 边界依据：[`architecture.md`](architecture.md):49,99-108,254、[`domain-analysis.md`](domain-analysis.md):98,105,154、[`eventbus-v2.md`](eventbus-v2.md):73,110,166
