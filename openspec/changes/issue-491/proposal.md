## Why

当审批与失败处理委托给 agent 后，owner 需要确信「agent 在处理」是真的：AgentJob 压根没跑起来或中途崩了时，必须收到通知，而不是以为有人管、其实没人管。同时，回看一个 issue 的审批史，owner 要能分清哪道门是 agent 放的、哪道是自己批的——否则无法判断该不该接手。两者同属「agent 的决定与缺位必须可见」，语义定稿见 `design/event-response.md`。

## What Changes

- AgentJob 进入终态失败（含 preflight 失败）时发射 `com.mohist.agent.job.failed` 事件，stamping 含 `agentid` 与业务谱系（issue / epic / workflowrunid，如有）。
- 新增通知种类「Agent 响应失败」：默认进入 inbox，默认进入 Hermes 推送（可关）。
- `agent.job.failed` 与其它事件同权进入路由协议；但指向失败 Agent 自身的路由规则（`rule.AgentId == 信封 agentid`）视同不命中，防止 agent 响应自己的失败（envelope-only 检查，记结构化日志）。
- 审批决议记录声明式操作者 `decidedBy`（与 comment 的 `author` 同模型：声明而非认证，必填、trim、≤100 字符）。
- **BREAKING**（对 agent / 集成方）：`mo run approve` / `mo run reject` 与对应 HTTP 端点增加必填的操作者入参；不带操作者的 approve/reject 调用将被拒绝。历史审批数据无 `decidedBy`，读取时按空兼容。
- 审批决议事件 `StageApprovalResolved`、`WorkflowRun` 领域方法、读取模型 `ApprovalStatusView` 均携带 `decidedBy`。

## Capabilities

- `agent-response-failure`: AgentJob 终态失败对外可见的契约——发射 `com.mohist.agent.job.failed` 事件、进入 inbox 与 Hermes 通知（新种类「Agent 响应失败」默认开启），以及该事件进入路由时的防自响应（规则 Agent 与信封 agentid 相同视同不命中）。
- `approval-attribution`: 审批决议的操作者归属——`decidedBy` 声明式字段贯穿 approve/reject 决议（领域方法、决议事件、读取模型、CLI `--author`、HTTP 入参），使审批历史能区分人与 agent。

## Impact

- **Server / Agent domain** (`packages/server/src/Mohist.Server/Agent/`):
  - `AgentJobGrain.EnterTerminalStateAsync`（`AgentJobGrain.cs:909`）：当前不发任何 CloudEvent，需在失败终态发射 `com.mohist.agent.job.failed`（grain 首次引入 `IEventStore` 依赖）。
  - 失败原因来源已汇聚于该入口（preflight、runner report、timeout、retry bound、forced fail）。
- **Server / Event 基础设施** (`Infrastructure/Events/`):
  - `EventCatalog.cs`：新增 `com.mohist.agent.job.failed` 类型常量与谱系规则；`ProducerConformance.cs` 新增 `AgentJob` producer family（`agentid` required，issue/epic/workflowrunid optional）。
  - `agentid` 谱系键已存在（`EventCatalog.cs:118`）。
- **Server / 路由自响应防护** (`Events/Subscriptions/RoutingDispatchHandler.cs`):
  - 在规则求值与启动之间新增 `rule.AgentId == 信封 agentid` 比对，命中则跳过并记结构化日志。
- **Server / Inbox** (`Inbox/`, `Events/Subscriptions/InboxProjectionHandler.cs`):
  - `NotificationKinds`（`InboxModels.cs:8`）新增种类并注册 `IsDefined`；`InboxSubscriptionState`（`:77`）默认开启。
  - `InboxProjectionHandler` 订阅新增类型并映射到新种类。
- **Server / Hermes** (`Notifications/`, `Events/Subscriptions/HermesIssueNotificationHandler.cs`):
  - `HermesNotificationOptions.EnabledTypes`（`:17`）默认开启新种类；handler 订阅新增类型；`HermesIssueNotificationRenderer` 新增渲染分支。
- **Server / Workflow 审批** (`Workflow/`):
  - `StageRun.ApprovalStatus`（`StageRun.cs:5`）、`WorkflowRun.Approve`/`Reject`（`WorkflowRun.Approval.cs:104,124`）、`StageApprovalResolved`（`WorkflowEvent.cs:40`）增加 `decidedBy`。
  - `WorkflowGrain.ApproveAsync`/`RequestChangesAsync`（`WorkflowGrain.cs:252,260`）增加操作者入参。
  - `ApprovalStatusView`（`WorkflowViews.cs:128`）与映射（`WorkflowStatusMapper.cs:41`）增加 `decidedBy`。
- **Server / API** (`Api/WorkflowRoutes.WorkflowControl.cs`, `Api/IssueRoutes.WorkflowControl.cs`):
  - approve/reject 请求体增加必填 `author`，镜像 comment 的 `AddCommentRequest`。
- **CLI** (`packages/cli/Mohist.Cli/MohistCliCommands.Run.cs`):
  - `BuildApprove`/`BuildReject` 增加 `--author` 选项，复用 comment 的本地校验（`MohistCliCommands.Issue.Comment.cs:18` 为模板）。
- **Web**（后续，不在本 issue 验收内）：审批历史展示可呈现 `decidedBy`；读取模型已携带该字段，UI 渲染为可选增强，本 issue 的验收以读取结果携带 `decidedBy` 为准。
- 测试：fake 轨道覆盖（a）AgentJob 失败 → inbox + Hermes；（b）防自响应；（c）approve/reject 带 author → 决议与读取模型携带；（d）历史数据无 decidedBy 的兼容读取。
