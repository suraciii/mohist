## Why

Mohist's workflow 自治推进每到一个审批门（plan / check）都要人到场 approve，issue 一多就成了瓶颈。Agent 目前只能手动启动——`POST /api/projects/{...}/agents/{...}/sessions`（`Api/AgentSessionLaunchRoutes.cs`）是唯一入口，对事件毫无反应能力。用户要的是一个通用能力：让任何 Agent 监听任何 CloudEvent、按事先配好的响应提示词自动醒来——替我盯审批门只是第一个场景。技术方案已在 `design/agent-subscriptions.md` 收敛（订阅归属 Agent 叶子域、消费 CloudEvent PL、按优先级单 Agent 响应、可见性取代强制冲突检测），前置依赖 `mo workflow get`（issue #381）已交付，现在可以落地。

## What Changes

- **新增 `AgentSubscription` 聚合**（Agent 域，1 Agent : N 订阅）：每条订阅声明 `Filter`（基于 CloudEvent 信封属性的表达式）+ `ResponsePrompt`（带 `{{workflow_run_id}}`/`{{stage}}`/`{{event_type}}` 占位符）+ 可选 `Priority` + `Status`(active/archived) + 元数据。订阅是一等对象，可独立增删、归档/恢复、命名。
- **新增订阅分发 handler**（`Events.Subscriptions`，与 `InboxProjectionHandler` 同层）：收到 CloudEvent → 查 active 订阅 → Filter 匹配信封属性 → 按 Agent 归组、组间按「组内最高订阅优先级」仲裁选一个 Agent → 组内按订阅优先级选一条 → 渲染响应提示词 → 启动 Agent。骨架照搬 `InboxProjectionHandler`，零业务域 `using`，纯 PL 消费。
- **事件级按优先级单 Agent 响应**：一个 CloudEvent 实例只被一个 Agent 响应（兜底低优先级 + 特定 issue 高优先级接管）。同优先级不报错、确定性选一个 + 可见性兜底——**不做严格冲突检测/拒绝**。
- **抽出 `IAgentLauncher` 内部 service**：把现散在 `Api/AgentSessionLaunchRoutes.cs:73-97` 的 `mint sessionId → OpenAsync → 构造 AgentJobInput → SubmitAsync` 提取出来，HTTP 层与新 handler 共用。纯提取，HTTP 手动启动行为不变。
- **双向可见性**：每次订阅触发的 Agent session 在 metadata 上打两个 trigger 标签（`mohist.io/trigger/event-id`、`mohist.io/trigger/subscription-id`，落 `GenericAgentSessionMetadata` 现有 label 机制）——从事件查响应它的 Agent/订阅，从 Agent session 查触发它的事件/订阅。
- **配置入口**：Web UI Agent 详情页新增 Subscriptions 分区（增删启停 + 列表）；CLI 新增 `mo agent subscribe` 等命令（create/list/delete）。
- **Agent 自拉上下文**：handler 只读信封自带字段渲染提示词；Agent 启动后用 `mo workflow get <runId>` 拉详情含关联 issue、`mo issue show` 读 proposal、走 `mo workflow approve/reject` 同一正规审批通道（裁判权不转移、不 bypass workflow）。
- **归档/删除语义**：归档的 Agent 不能挂新订阅、其名下已有订阅停止触发；归档的订阅不触发；删除/归档一条订阅不影响它已触发、正在运行的 session（让其跑完）。

Non-goals（见 issue 正文 + `design/agent-subscriptions.md`「不做」）：审批可追溯结构化字段、严格冲突检测、per-订阅重试/outbox、per-Agent 并发闸门强制、完整 CloudEvents Subscriptions API filter dialect、Skill 授权。

## Capabilities

- `agent-subscription-management`: Subscription 作为 Agent 域一等对象的声明与生命周期——字段（name/filter/response-prompt/optional priority/status）、CRUD、active/archived 状态切换、以及守界生命周期不变量（归档 Agent 拒绝新订阅且其名下订阅停止触发；归档订阅不触发；删除/归档订阅不影响已在跑的触发会话）。
- `agent-subscription-dispatch`: 订阅触发的运行时管线——CloudEvent 到来后按 Filter 表达式匹配信封属性（type/source/subject，沿用 `|`/`*`/`.*` 语义扩展到多属性）、按 Agent 归组并以「组内最高订阅优先级」做事件级仲裁（一个事件一个 Agent，同优先级确定性选一个不拒绝）、渲染响应提示词（信封自带占位符）、经共享 `IAgentLauncher` 启动 Agent 并完成两层提示词合成（Agent 身份指令 + 订阅响应提示词）。
- `agent-subscription-visibility`: 订阅触发的双向可观测性——session metadata 的 trigger 标签（event-id + subscription-id），支撑「从事件查响应 Agent/订阅」与「从 Agent session 查触发事件/订阅」两条查询方向，作为取代严格冲突检测的核心防错机制。
- `agent-subscription-config-surface`: 订阅的用户配置入口——Web UI Agent 详情页 Subscriptions 分区与 CLI create/list/delete 命令面。

## Impact

- **Agent 域（`packages/server/src/Mohist.Server/Agent/`）**：新增 `AgentSubscription` 聚合 + grain + Store（复刻 `InboxSubscription` 模式，表 + Store + `ListByProjectAsync`/`ListByAgentAsync`）；新增 `IAgentLauncher` service，把 `AgentSessionLaunchRoutes.cs:73-97` 的 mint/open/build-input/submit 链路提取到此；`AgentJobGrain`/`AgentJobInput` 不变（launcher 仍走 `SubmitAsync`）。
- **事件分发（`packages/server/src/Mohist.Server/Events/Subscriptions/`）**：新增 `[Subscription]` handler（`*` 全类型，自行用订阅 Filter 过滤），骨架照搬 `InboxProjectionHandler`；新增 Filter 匹配器（扩展现有 `InMemoryEventBus.cs:95-109` 的 `|`/`*`/`.*` 到 type/source/subject 多属性）与优先级仲裁器；轻量模板变量渲染（字符串替换，不引入模板引擎）。
- **Session 可见性（`packages/server/src/Mohist.Server/Sessions/Services/GenericAgentSessionMetadata.cs`）**：新增 `mohist.io/trigger/event-id`、`mohist.io/trigger/subscription-id` 两个 label key。
- **EventCatalog（`Infrastructure/Events/EventCatalog.cs`）**：补登记本功能可订阅的 issue.* / workflow.* 事件类型（供 UI 配置发现），不改生产侧。
- **API（`packages/server/src/Mohist.Server/Api/`）**：新增订阅 CRUD 端点（project + agent 寻址）；`AgentSessionLaunchRoutes` 改为调用 `IAgentLauncher`。
- **CLI（`packages/cli/`）**：新增 `mo agent subscribe`/`subscription list`/`subscription delete` 等命令。
- **Web（`packages/web/`）**：Agent 详情页新增 Subscriptions 分区。
- **边界**：Agent 保持叶子域——handler 零业务域 `using`，CloudEvent 作为 PL 消费；不改 workflow/issue 事件生产侧、不盖印；不碰 workflow profile 的 `requiresApproval`。
- **前置依赖**：`mo workflow get` 返回关联 issue（issue #381，已交付）。无 schema 迁移到核心域；订阅表是新持久化。
- **Risk**：medium——新增事件反应式能力横跨 server(grain+handler+API)+CLI+Web，但不动 workflow 引擎与裁判权，仲裁/可见性策略可逆可调。
