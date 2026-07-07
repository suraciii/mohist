## Context

Mohist's workflow 自治推进在每个审批门（plan/check）强制等人 approve，issue 一多就成瓶颈。Agent 目前只能从单一人肉入口启动——`POST /api/projects/{...}/agents/{...}/sessions`（`Api/AgentSessionLaunchRoutes.cs`），对事件毫无反应。本变更给 Agent 加反应式能力：监听 CloudEvent、按用户配的订阅响应提示词自动启动。

技术方案已在 [`design/agent-subscriptions.md`](../../../design/agent-subscriptions.md) 收敛（订阅归属 Agent 叶子域、消费 CloudEvent PL、按优先级单 Agent 响应、可见性取代强制冲突检测）。本文件是该技术方案的**实施设计**——HOW 落地，不重复 WHY（见 proposal）与 WHAT（见 specs）。

关键现状（动手前要重读的源码锚点）：

- **手动启动链路**：`Api/AgentSessionLaunchRoutes.cs:73-97` —— `NewSessionId → OpenAsync(OpenAgentSessionCommand) → 构造 AgentJobInput → IAgentJobGrain.SubmitAsync`。这段目前散在 HTTP route 里，没有共享的 service 入口。
- **session metadata label 机制**：`Sessions/Services/GenericAgentSessionMetadata.cs:36-65` —— 现有 5 个 `mohist.io/*` key（agent-id/agent-name/issue-number/epic-number/repository/workspace-path），通过 `Metadata(context)` 写入、`LookupLabels` 暴露。
- **现有 CloudEvent 订阅范式**：`Events/Subscriptions/InboxProjectionHandler.cs` —— `[Subscription(...)] + ICloudEventHandler`、`IServiceScopeFactory` 解析 scoped store、`HandleAsync` 内 try/catch 吞异常。这是新 handler 的骨架模板。
- **现有 Filter 匹配**：`Infrastructure/Events/InMemoryEventBus.cs:95-109` 的 `Matches(pattern, type)` —— 支持 `|` 多选 / `*` 全匹配 / `prefix.*` 后缀通配（仅 `.*` 结尾），但**只对 `type`**。
- **现有订阅存储范式**：`Inbox/InboxSubscriptionStore.cs` —— 一个 project 一份订阅配置，`GetAsync/SetAsync`。
- **前置依赖已交付**：issue #381 的 `mo workflow get <runId>` 返回 `WorkflowRunDetailDto.IssueRef`（关联 issue 号 + 标题），Agent 启动后据此自拉上下文，无需 handler 反查。

约束：

- **Agent 是叶子域**（`design/domain-analysis.md:98,105`）—— handler 零业务域 `using`，纯 PL 消费。
- **CloudEvent 是 Published Language**（`design/eventbus-v2.md:110,166`）—— `[Subscription]`+`ICloudEventHandler` 是基础设施投递落点。
- **裁判权不转移** —— Agent 自动 approve 走和人类一样的正规通道，不 bypass workflow。
- 项目正处积极开发，无版本兼容包袱（AGENTS.md）。

## Goals / Non-Goals

**Goals:**

- 把 `AgentSubscription` 落成 Agent 域一等聚合（1 Agent : N 订阅），独立增删启停。
- 落一个订阅分发 handler：收到 CloudEvent → Filter 匹配信封属性 → 按 Agent 归组、组间按「组内最高优先级」事件级仲裁、组内按订阅优先级选一条 → 渲染响应提示词 → 经共享 launcher 启动 Agent。零业务域 `using`。
- 抽出 `IAgentLauncher` 内部 service，HTTP 层与新 handler 共用；HTTP 手动启动行为不变。
- 双向可见性：session metadata 上打 `mohist.io/trigger/event-id` + `mohist.io/trigger/subscription-id` 两个 label。
- 提供 Web UI Agent 详情页 Subscriptions 分区 + CLI `mo agent subscribe/list/delete`（或等价命令面）。

**Non-Goals:**

- 审批可追溯结构化字段（MVP 不做，记为已知缺口，后续 issue 评估）。
- 严格冲突检测/拒绝——同优先级多命中不报错、确定性选一个 + 可见性。
- per-订阅重试/outbox——复用事件总线现有投递 + AgentSession 失败可见。
- per-Agent 并发闸门（`MaxConcurrentRuns` 强制）。
- 完整 CloudEvents Subscriptions API filter dialect。
- 改 workflow/issue 事件生产侧（不盖印）。
- 重构现有 Agent 手动启动链路（只共享底层 launcher）。

## Decisions

### D1 — `AgentSubscription` 聚合落 Agent 域，存储复刻 `InboxSubscriptionStore` 但索引不同

新聚合放 `packages/server/src/Mohist.Server/Agent/Domain/AgentSubscription.cs`，grain/store 放 `Agent/` 下，对齐 `Agent` 实体的目录归属。

字段（与 `design/agent-subscriptions.md`「订阅模型」一致）：

```
AgentSubscription
  Id, ProjectId, AgentId, Name (unique within Agent),
  Filter (string, 见 D2),
  ResponsePrompt (string, 带 {{workflow_run_id}} {{stage}} {{event_type}} 占位符),
  Priority (int?, null 取默认 0),
  Status (active|archived),
  CreatedAt, UpdatedAt
```

存储**不**照搬 `InboxSubscriptionStore` 的「1 project 1 配置」单行模型——订阅是多行、多索引对象。改用 `IStateStore<AgentSubscription>` + 一个轻量索引（参考其他多行聚合的做法）：

- `GetAsync(id)` → 单条
- `ListByAgentAsync(projectId, agentId)` → 配置面（Web/CLI 列出某 Agent 的订阅）
- `ListByProjectAsync(projectId)` → dispatch handler 拿候选集

**Why 不嵌入 Agent 定义**：spec `agent-subscription-management` 明确要求订阅独立可寻址、增删不碰 Agent 定义。嵌入会让 archive 一条订阅变成改 Agent 聚合（Orleans grain 串行化开销 + Agent 状态膨胀）。

**Alternative considered**：把订阅做成 Agent grain 内的子集合（Agent grain 持有 `List<AgentSubscription>`）。否决——dispatch handler 要跨所有 Agent 扫订阅，逐个 grain 调用比一次 `ListByProjectAsync` 贵得多，且 archive 订阅要锁 Agent grain。

### D2 — Filter 表达式数据模型：单字符串 + 多属性分隔符（不引入结构化对象）

Filter 存成**一个字符串**，语法沿用 `InMemoryEventBus.Matches`（`Infrastructure/Events/InMemoryEventBus.cs:95-109`）已有的 `|` / `*` / `prefix.*`，**扩展到多属性**。

约定最简形式（覆盖大多数场景）：一个 type 表达式，如 `com.mohist.workflow.stage.approval-requested` 或 `com.mohist.workflow.stage.*`。

需要约束 source/subject 时（精确到某 issue 的 run），用一个轻量多属性分隔语法。两种可选：

- **(倾向) 复合字符串**：`type:com.mohist.workflow.stage.* | source:/mohist/workflow-runs/run_xxx` —— 解析简单，但多属性时需小心 `|` 的作用域（`|` 是属性内 OR，属性间是 AND）。
- **结构化子字段**：`AgentSubscription.Filter` 改成小 record `Filter { Type, Source?, Subject? }`，每字段独立用 `|`/`*`/`.*` 语义。

倾向先落**结构化子字段**（decision 修正了 design doc 的开放项 #1）：`Type` 字段保留全语义（`|`/`*`/`.*`），`Source`/`Subject` 字段只支持精确匹配（spec `agent-subscription-dispatch` 的 source/subject scenario 只要求精确匹配）。理由：(a) 避免 `|` 作用域歧义；(b) 持久化天然可索引（按 source 精确反查某 issue 的订阅变可能）；(c) matcher 实现更直白。EF 映射三列即可。

**Why 不上完整 CloudEvents Subscriptions API dialect**：spec 明确 non-goal「完整 filter dialect」。简易扩展够覆盖兜底/接管场景，待需求驱动再升级。

**Matcher 实现**：新建 `Events/Subscriptions/SubscriptionFilter.cs`，`bool Matches(CloudEvent evt)`。`Type` 字段复用 `InMemoryEventBus.Matches` 的算法（提取为可复用静态方法，或照抄——逻辑只有 12 行）；`Source`/`Subject` 做 `string.Equals` 精确比较，null/空 表示不约束。**匹配只在信封属性上发生，零业务域查询**——这是守边界的硬约束（spec `agent-subscription-dispatch`）。

### D3 — 事件级仲裁：按 Agent 归组 + 组间/组内双层级 + 确定性 tie-break

每个 CloudEvent 实例到来，dispatch handler 执行（spec `agent-subscription-dispatch` 要求 exactly one (Agent, subscription)）：

```
1. ListByProjectAsync(projectId) → 取本 project 全部订阅
2. 过滤：Status == active 且 owning Agent.Status == active（D6 守界）
3. 逐条 SubscriptionFilter.Matches(evt) → 得命中集
4. 按 AgentId 归组：{AgentA: [s1, s2], AgentB: [s3]}
5. 组间仲裁：每组 score = max(订阅 Priority) 在该组内；取 score 最高组（一组赢）
6. 组内仲裁：赢组内取 Priority 最高的单条订阅
7. tie-break：Priority 相同时按 SubscriptionId 字典序最小（确定性、可复现）
8. 命中空 → 不触发、不报错（spec scenario "No match"）
```

**Why 按 Agent 仲裁而非按订阅仲裁**：同一 Agent 配多条订阅（不同提示词应对不同子场景）是合法配置，按订阅仲裁会把这当成「多 Agent 响应」而违反「一个事件一个 Agent」约束（`design/agent-subscriptions.md`「Priority + 事件级仲裁」节）。

**Why 用 SubscriptionId 字典序 tie-break**：稳定、可复现、零额外字段、对用户可读（spec `agent-subscription-dispatch`「Equal-priority ties」scenario 要求 deterministic + reproducible）。

**Alternative considered**：用 CreatedAt 做 tie-break。否决——时钟可能同毫秒，仍需次级 tie-break；Id 字典序一步到位。

**Priority null 语义**：null 取默认 `0`。这样用户不填 Priority 时所有订阅同优先级，由 tie-break 确定性选一个——满足 spec「Priority is optional」scenario。

### D4 — 抽 `IAgentLauncher` service，纯提取 `AgentSessionLaunchRoutes.cs:73-97`

新接口 `Agent/Services/IAgentLauncher.cs`：

```csharp
Task<AgentLaunchResult> LaunchAsync(
    Agent agent,
    string prompt,
    AgentLaunchContext context,         // workspace path 等
    IReadOnlyDictionary<string, string>? triggerLabels = null,   // D6
    CancellationToken ct = default);
```

实现 `AgentLauncher.cs` 把现散在 `AgentSessionLaunchRoutes.cs:73-97` 的链路（`NewSessionId → OpenAsync → AgentJobInput → SubmitAsync`）整体搬过来。HTTP route 改成注入 `IAgentLauncher` 调用，行为/响应/状态码不变。

`triggerLabels` 参数：HTTP 路径传 null（不打 trigger label）；dispatch handler 传 `{event-id, subscription-id}`（D6）。launcher 把 triggerLabels 合并进 `GenericAgentSessionMetadata` 现有 label 集合。

**Why 单独抽 service 而非让 handler 直接调 grain**：(a) spec `agent-subscription-dispatch`「reuses the shared Agent launcher」要求共享；(b) 两处启动要打的 metadata 不同（手动无 trigger label，订阅有），共用入口才能统一 metadata 装配；(c) HTTP route 现有逻辑是「mint session → open → build input → submit」，handler 复刻一遍会引入双份维护。

**Alternative considered**：把 launcher 做成 static helper。否决——它需要 `ISessionFactory`、`IGrainFactory` 等 DI 依赖，static 化会强制 service locator 反模式。

**Lifetime**：`IScopedService`（与 `IssueQuerier` 等同模式，参考 archive issue-325 D2）。handler 通过 `IServiceScopeFactory` 在 `HandleAsync` 内开 scope 解析（同 `InboxProjectionHandler.cs:114` 模式）。

### D5 — Dispatch handler 骨架照搬 `InboxProjectionHandler`，`[Subscription("*")]` 全收

新文件 `Events/Subscriptions/AgentSubscriptionDispatchHandler.cs`：

```csharp
[Subscription(Type = "*")]   // 全收，自行用 SubscriptionFilter 过滤
public sealed class AgentSubscriptionDispatchHandler : ICloudEventHandler
{
    // 构造：IServiceScopeFactory + ILogger（同 InboxProjectionHandler）
    public bool Filter(CloudEvent evt) => evt is not null;  // 全收，HandleAsync 内自行筛
    public async Task HandleAsync(CloudEvent evt, CancellationToken ct)
    {
        try { await DispatchAsync(evt, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { _log.LogWarning(ex, ...); }  // 吞异常，不阻塞总线
    }
}
```

**Why `[Subscription("*")]` 而非枚举具体 type**：订阅的 Filter 由用户配，handler 启动时不知道用户会订哪些 type。全收 + 内部自行 SubscriptionFilter 匹配是唯一可行方案。这跟 `InboxProjectionHandler`（固定 4 个 type）不同——后者是基础设施硬编码的固定投影，本 handler 是用户配置驱动的动态匹配。

**Why `IServiceScopeFactory` 而非构造注入**：`InboxProjectionHandler.cs:174-182` 已记录这个 DI 陷阱——bus 把 handler 注册成 singleton，构造期解析 `IEventPublisher` 会与 bus 自身构造形成环（bus 构造时枚举所有 handler 订阅）。即便本 handler 不发事件，也要遵循同模式解析 scoped store（`AgentSubscriptionStore` 是 scoped）。

**HandleAsync 主体**：

```
1. 从 evt extensions/source 解析 projectId（workflow 事件从 source 的 runId → 注解查；issue 事件从 extensions["projectid"]）
   —— 复用 InboxProjectionHandler 的解析逻辑思路，但只取 projectId，不查 issue 标题
2. var subs = await store.ListByProjectAsync(projectId) → D3 步骤 2-7 仲裁
3. 若选出 (agent, subscription)：
     renderedPrompt = RenderPrompt(subscription.ResponsePrompt, evt)   // D7
     await launcher.LaunchAsync(agent, renderedPrompt, ctx, triggerLabels: {event-id, subscription-id})
```

**projectId 解析的边界合规性**：从 CloudEvent 信封自带的 extensions/source 取 projectId 是 PL 消费，**不是**业务域反查。`InboxProjectionHandler` 也做同样解析（它后续还多查了 issue 标题，那才是业务反查——本 handler 不查）。

> **实施时确认点**：workflow 事件的 projectId 当前在 `InboxProjectionHandler` 里是通过 `IWorkflowRunStore.LoadAsync` 读 `WorkflowRunMetadata.Annotations["projectId"]` 拿到的（行 200-220），这**是**业务域读侧调用。为保持 Agent 叶子域纯净，本 handler 不照搬——改为依赖 workflow 事件**信封 extensions** 已带 `projectid`（若没带，需要在事件生产侧补一个无业务语义的 projectId 盖印到 extensions，或在 handler 接受「无法解析 projectId 则跳过」的降级）。这是本设计与 design doc 的一个落地差异点，列入 Open Questions。

### D6 — Trigger label 落 `GenericAgentSessionMetadata`，扩展两个 key

`Sessions/Services/GenericAgentSessionMetadata.cs` 新增两个 const（与现有 key 同风格）：

```csharp
public const string TriggerEventId = "mohist.io/trigger/event-id";
public const string TriggerSubscriptionId = "mohist.io/trigger/subscription-id";
```

launcher（D4）在装配 metadata 时，把 `triggerLabels` 字典合并进现有 label 集合。HTTP 路径 triggerLabels=null → 这两个 key 不出现（spec `agent-subscription-visibility`「Manually launched sessions carry no trigger labels」scenario）。

**双向查询的实现**：spec `agent-subscription-visibility` 要求两条查询方向。这两个 key 落 session metadata 的现有 label 机制后：

- **event → session**：现有 session 查询接口按 label 过滤（`mohist.io/trigger/event-id == {evtId}`）即可。
- **session → event**：session 详情已暴露 metadata，直接读 label。

**Why 不单独造 trigger 跟踪表**：spec 明确「without additional structured tracking tables」——复用现有 label 机制即可 join。新建表会引入双写一致性问题。

### D7 — 模板变量渲染：纯字符串替换，三个变量，零模板引擎

新建 `Events/Subscriptions/ResponsePromptRenderer.cs`（或做成 `AgentSubscription` 的实例方法）：

```
RenderPrompt(string template, CloudEvent evt) → string
  template.Replace("{{workflow_run_id}}", ExtractRunId(evt.Source))
         .Replace("{{stage}}",             ExtractStage(evt.Data))
         .Replace("{{event_type}}",        evt.Type ?? "")
```

- `{{workflow_run_id}}`：从 `evt.Source`（`/mohist/workflow-runs/{runId}`）解析，复用 `WorkflowStageLockReleaseHandler.ExtractWorkflowRunId`（`InboxProjectionHandler.cs:191` 已在用）。
- `{{stage}}`：从 `evt.Data` 的 `Stage` 字段（workflow 事件 payload 自带）。
- `{{event_type}}`：`evt.Type`。

**未命中占位符**：spec 要求「leave as-is or empty, deterministically」。选**保留原文**（不替换）——这样用户配错变量名能在 prompt 里直接看到，比静默吞掉更利于调试。

**Why 不提供 `{{issue}}`**：spec `agent-subscription-dispatch`「Response prompt is rendered from envelope-carried variables」明确「SHALL NOT provide an `{{issue}}` variable」。issue 号由 Agent 执行 `mo workflow get` 后从 `IssueRef.Number` 获取——这是「Agent 自拉上下文」边界的体现。

**Why 不引入模板引擎**（Handlebars.NET 等）：三个变量、纯替换，引入引擎是 over-engineering。spec 明确「SHALL NOT introduce a template engine」。

### D8 — 守界生命周期：archive Agent 拦创建 + 阻触发，archive 订阅阻触发

spec `agent-subscription-management` 三条生命周期不变量：

1. **archived 订阅不触发** → D3 步骤 2 已过滤 `Status == active`。
2. **archived Agent 拒新订阅 + 其名下订阅停止触发** → 创建订阅时查 Agent.Status，archived 返回 conflict（HTTP 层 `AgentSessionLaunchRoutes.cs:68-71` 已有同模式 `agent_archived` 错误码，复用）。dispatch handler D3 步骤 2 同时过滤 owning Agent `Status == active`。
3. **archive/delete 订阅不影响在跑 session** → launcher 调完 `SubmitAsync` 就脱钩，session 生命周期归 AgentJobGrain 管，与订阅存储解耦。无需额外代码——架构天然满足。

**Why dispatch 时再查 Agent.Status 而非靠订阅 Status 联动**：archive Agent 时不去批量改订阅 Status（spec 要求订阅保留自身 active status）。dispatch 时实时查 Agent 状态是唯一正确做法。

### D9 — Config surface：Web + CLI 复用现有 Agent CRUD 的身份解析

- **CLI**（`packages/cli/`）：新增命令，复用现有 `mo agent` 的 project 解析与 agent name/id 解析（id 前缀 → by id，否则 name then id）。命令面：create（name + filter + response-prompt + 可选 priority）、list、delete。response-prompt 支持 inline / `--file` / stdin（与现有接受长文本的命令同模式）。
- **Web**（`packages/web/`）：Agent 详情页新增 Subscriptions 分区——list/create/archive/restore/delete。用 TanStack Query mutation 失效 Agent detail query，列表无手动刷新（spec `agent-subscription-config-surface`「without a manual refresh」）。
- **API**（`packages/server/src/Mohist.Server/Api/`）：新增 `AgentSubscriptionRoutes.cs`（CRUD），寻址 `projects/{projectId}/agents/{agentRef}/subscriptions`。复用 `AgentRefResolver`（`AgentSessionLaunchRoutes.cs:63` 已在用）。

**Why 不为订阅单独造身份模型**：spec `agent-subscription-config-surface` 明确「SHALL NOT introduce a separate identity or scoping model distinct from the Agent's」。

## Risks / Trade-offs

- **[workflow 事件信封未带 projectId → handler 无法解析 → 跳过触发]** → 见 D5 实施确认点。Mitigation：实施时先确认 workflow/issue 事件 extensions 是否已带 `projectid`；若没带，评估是否接受「无法解析即跳过」降级（部分订阅静默失效，靠可见性发现），或在事件生产侧补一个**无业务语义**的 projectId 盖印到 extensions（不污染核心域模型，只是 PL 信封补字段）。倾向后者。
- **[InMemoryEventBus 单线程投递，慢 handler 阻塞总线]** → dispatch handler 调 launcher → grain 调用，若 AgentJobGrain.SubmitAsync 慢会拖累所有事件投递。Mitigation：launcher 的 `SubmitAsync` 应只入队（mint session + enqueue job），不阻塞等 runner；这已是现有手动启动的行为，提取到 launcher 后保持。实施时验证 SubmitAsync 是 fire-and-forget 入队。
- **[同 Agent 多订阅 + 同优先级 → 确定性但可能非用户预期]** → tie-break 选 SubscriptionId 字典序最小，用户可能没意识到。Mitigation：spec 明确「可见性是主要防错机制」——session 上打 trigger-subscription-id，用户能查到「实际是哪条订阅触发的」并据此调 Priority。不报错不阻塞是 spec 要求。
- **[Store 多索引一致性]** → `ListByProjectAsync` / `ListByAgentAsync` 若用反范式索引需保证写时同步。Mitigation：MVP 用单表 + 内存 LINQ 过滤（订阅量预期小，单 project 几十条），不建反范式索引；性能问题待需求驱动。
- **[trigger label 泄漏到手动 session]** → launcher 装配 metadata 时若 triggerLabels 字典合并逻辑写错，手动路径可能误打。Mitigation：`triggerLabels` 参数默认 null，HTTP 路径不传；spec `agent-subscription-visibility`「Manually launched sessions carry no trigger labels」作为单元测试断言。
- **[CloudEvent 投递重放 → 同事件触发多次]** → 未做 per-订阅 dedup（non-goal）。Mitigation：复用事件总线现有投递语义；AgentSession 失败可见；若实际出现重放问题，后续 issue 加 outbox/dedup。
- **[launcher 提取破坏 HTTP 行为]** → 纯提取但有回归风险。Mitigation：spec `agent-subscription-dispatch`「Manual HTTP launch behavior is preserved」scenario 作为回归测试门禁；提取后 HTTP 路径的响应/状态码/副作用必须与现状逐字段一致。

## Migration Plan

单 PR、单次部署。无核心域 schema 迁移——订阅表是新持久化。前置依赖 `mo workflow get`（issue #381）已交付。

落地顺序（每步可独立编译验证，对齐 `design/agent-subscriptions.md`「落地顺序」）：

1. **`IAgentLauncher` 提取（D4）** —— 纯机械提取 `AgentSessionLaunchRoutes.cs:73-97` 到 `AgentLauncher`；HTTP route 改调 launcher；HTTP 启动行为逐字段回归（spec「Manual HTTP launch behavior is preserved」）。可独立合并验证。
2. **`AgentSubscription` 聚合 + Store + CRUD API（D1）** —— Domain 实体 + Store（`ListByProjectAsync`/`ListByAgentAsync`/`GetAsync`/`SetAsync`）+ `AgentSubscriptionRoutes.cs`（create/list/get/update/archive/restore/delete）。守界规则（D8 archive Agent 拒创建）在此落地。
3. **Filter matcher + 仲裁器 + dispatch handler（D2, D3, D5, D7）** —— `SubscriptionFilter.Matches`、`ResponsePromptRenderer`、`AgentSubscriptionDispatchHandler`（`[Subscription("*")]`）。纯信封消费，零业务域 `using`。D5 的 projectId 解析确认点在此暴露并决策。
4. **可见性 label（D6）+ EventCatalog 补登记 + Web/CLI 配置面（D9）** —— `GenericAgentSessionMetadata` 加两 key、launcher 合并 triggerLabels；EventCatalog 补 issue.* / workflow.* 可订阅类型；CLI 命令、Web Subscriptions 分区。

**验证门禁**：
- server：`npm test`（C# `TreatWarningsAsErrors` 当 lint；新增 spec 覆盖守界、仲裁、可见性、生命周期不变量）。
- web：`npm run typecheck -w packages/web` + `npm run test:run -w packages/web`。
- runner：本变更不动 runner，`npm run typecheck -w packages/runner` 仅作回归。
- HTTP 手动启动逐字段回归（spec「Manual HTTP launch behavior is preserved」）。

**Rollback**：回退单 PR。订阅表是新持久化，回滚后残留订阅数据不影响系统（无订阅消费方）。已触发、在跑的 Agent session 不受影响（其生命周期归 AgentJobGrain，与订阅存储解耦）。

## Open Questions

- **workflow/issue 事件信封是否已带 `projectid` extension？** （D5 实施确认点）—— 若没带，dispatch handler 无法仅靠信封解析 projectId。倾向方案：在事件生产侧补一个**无业务语义**的 `projectid` 盖印到 CloudEvent extensions（PL 信封字段，不污染核心域模型）。实施时先 grep 事件生产侧确认现状，再决策。
- **`AgentSubscription` 持久化用 EF 还是复刻 `InboxSubscriptionStore` 的 JSON 文件/单表模式？** —— `InboxSubscriptionStore` 是「1 project 1 行」单配置；订阅是多行多索引。倾向 EF + 单表 + 内存过滤（订阅量小），但需确认 server 现有持久化约定（`design/conventions.md`）。实施时重读 Inbox/Workflow 两个 Store 的持久化模式选其一。
- **CLI 命令命名**：`mo agent subscribe`（动词式）vs `mo agent subscription create`（名词式）？ 现有 `mo agent` 命令面需对齐。倾向后者（与 `mo workflow approve` 等名词-动词结构一致），实施时看现有 `mo agent` 子命令风格定。
- **Filter 的 `Source`/`Subject` 字段是否需要 `|` 多选？** —— D2 倾向只精确匹配。但若用户想「盯 issue #42 或 #43 的 run」，source 多选有用。MVP 先精确，待需求驱动。
