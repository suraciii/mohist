# Web UI

Mohist's backup operation and visualization plane. It presents authoritative execution state, evidence,
relationships, and safe user actions when the owner needs a global view or must take over manually.

## Product boundary

- The user's primary conversation usually stays in Slack, an IDE, or another external surface. Web does
  not recreate those products, but it provides a complete direct Mohist Agent client for configuration,
  launch, follow-up, Job result, Session evidence, and recovery.
- Backup means infrequent, not incomplete. Critical lifecycle, approval, recovery, and configuration
  actions remain available without an external Agent.
- Web emphasizes relationships and evidence that are hard to understand from a short Agent summary:
  project attention, Issue and Epic progress, Workflow state, diffs, AgentSession transcripts, and system
  health.
- New domain actions cannot exist only in Web. Web submits the same server-owned intent available to other
  clients and never creates a second interpretation of Workflow state.
- Web, CLI and Slack adapter consume the same Agent API. Web cannot add launch-time Agent config overrides
  merely because it owns the editor UI.

## What belongs where

| Concern | Owner |
|---|---|
| render state | Web UI |
| user actions | Web UI → API |
| authoritative state | Server |
| workflow decisions | WorkflowGrain |
| shell/agent/git execution | Runner |
| realtime push | Server → Web UI |
| Agent definition and AgentJob result | Agent context via Agent API |
| Agent Connection binding and policy | Agent context via API |

Web UI never interprets workflow rules. It shows server state and submits user intent.

## Events

Push is observation, not driver. SignalR (`/hubs/events`). UI reconnects → self-reconcile.

```
WorkflowGrain commits → server persists/publishes → SignalR → Web UI refreshes queries
```

## Routes

UI 与 API 都使用领域身份路径：`/projects/{projectId}/issues/{issueNumber}` 和
`/projects/{projectId}/epics/{epicNumber}`。Issue / Epic number 不再解析成另一套内部 id；
WorkflowRun 继续使用 `workflowRunId`。

## Rules

- Query hooks own data fetching and cache invalidation.
- UI state stores view prefs, filters, drafts. Never workflow truth.
- Runner details stay behind API. UI never depends on process internals.

## Agent product surface

Agent list/detail is a management and test surface, not a decorative catalog. It must expose definition,
launch, separate Job/Session status, and Connections without requiring users to infer relationships from
raw transcript events.

Identity, lifecycle, configuration Readiness, execution availability and Connection health are separate
signals. The UI never turns an offline Runner into an Agent configuration error or collapses Slack health
and Agent Readiness into one badge. Missing configuration points to the place where the Agent is edited;
Unknown remains visibly different from Ready and Needs setup.

Direct launch uses the same Agent API request as CLI and Slack except for authenticated actor/source
metadata. Agent fields are edited before launch; the composer only accepts prompt, context refs and
attachments. Runtime/Model/Skills overrides do not belong in the composer.

AgentSession page renders two modes from the same Session model:

- Workflow source emphasizes task ownership, evidence and recovery.
- Agent launch source also provides a complete follow-up composer and is the backup direct conversation
  client.

会话时间线的呈现模型（条目句式、领域动作识别、折叠与显著性纪律、原始视图）见
[`session-timeline.md`](session-timeline.md)。

The route mode cannot change Session lifecycle or API. AgentJob result is displayed separately from
Session activity and Turn progress. Connection setup and health belong on Agent detail because the user
starts from the Agent they intend to expose.

The Connection panel presents resumable setup, next action, access policy, identity alignment and health.
Allowlist editing uses member names and avatars as the human-facing control; display names are never used
as authorization identity, and Web never reads Slack tokens.

## 前端模块边界

Web 按 Feature-Sliced Design 组织为 `app`、`pages`、`widgets`、`features`、`entities` 和
`shared`。依赖只能从高层指向低层；同层切片不直接依赖，实体之间确有模型关系时才通过
`entities/<entity>/@x` 声明窄契约。

- `app` 只负责启动、Provider 和路由组合。它通过 page 或 widget 的 `index.ts` 消费路由
  页面和应用壳，不读取其 `ui` 或 `model` 内部文件。
- `pages` 拥有仅在一个路由内成立的交互和状态。Settings 搜索依赖 Settings 路由、tab 和
  焦点目标，因此属于 `pages/settings`，不是可复用 feature。
- `shared` 放置无业务归属的浏览器能力。Theme context 和快捷键声明/注册表由此层提供；
  `app` 负责挂载 ThemeProvider，具体页面和通用组件只消费 shared API。
- 多个领域 API 共用的静态筛选值属于 `shared/config`；资源不存在的展示属于
  `shared/ui`。路由 page 只负责把这些通用能力放在相应的路由入口。
- 切片对外只导出稳定的页面、组件或领域契约。内部 `ui`、`model`、`api` 路径不能成为
  跨切片导入目标。

## Preference

Dense, scannable screens. No landing pages or chat-first application composition. A direct AgentSession
may use a conversation layout because conversation is the task on that route; it does not become the app
home page.

First screens: attention-first production overview → Issue execution detail → approval and recovery →
execution evidence → runner status.
