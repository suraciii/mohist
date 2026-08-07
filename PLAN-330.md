# PLAN-330 — Slack / Web 入口的 workspace 自动解析

> 状态：**实施计划（只规划，不写实现代码）**。基于 master 实际代码盘点，非臆测。
> 权威 spec：`design/workspace.md`、`docs/workspaces.md`；辅助 `design/slack.md`、`design/repositories.md`、`design/testing.md`。

---

## 0. 编号与范围前置说明（需主 agent 知悉）

- 实时 issue tracker 中 **#330 = 「Session 读侧迁出 Workflow 目录」**（已 done，见 `openspec/changes/archive/2026-07-03-issue-330`）。本工作区名 `impl-ws-330-slack-web` 与之冲突。**本计划针对的功能 = 「Slack / Web 入口的 workspace 自动解析」**，按 prompt 给定的范围/验收执行；issue 编号是否需在 tracker 修正，见 §6 决策 D0。
- 功能本身在 spec 中定义清晰、无歧义，故本计划不阻塞于编号核对。

---

## 1. 现状盘点（引用具体文件 / 类型）

### 1.1 Workspace 聚合基座（已落地，slack/web 是占位）

| 维度 | 现状 | 文件 |
|---|---|---|
| 领域模型 | `WorkspaceState{ProjectId,Name,Origin,RepositoryNames,Status,Home,CreatedAt,ArchivedAt}`；`WorkspaceOrigin = Manual \| Issue(n) \| Slack(teamId,channelId) \| Web(conversationId)` —— **四种 origin 均已在模型定义** | `Workspace/Domain/Workspace.cs` |
| Grain 实装方法 | `CreateManualAsync` / `EnsureIssueWorkspaceAsync(n,repo,now)` / `ArchiveByIssueAsync(n,now)` / `CloseAsync(now)` / `AddRepo` / `RemoveRepo` / `GetHome` / `EnsureMaterializedOnAsync` / `ClearHomeIfAsync` | `Workspace/Grains/WorkspaceGrain.cs`、`IWorkspaceGrain.cs` |
| **slack/web 缺口** | **无 `EnsureSlackWorkspaceAsync` / `EnsureWebWorkspaceAsync` / 对应 Archive 方法**。`BuildEvent` 的 origin stamping（created/archived 事件 payload）**已覆盖 slack/web**，创建路径一落地即带谱系 | 同上 |
| 校验 | `WorkspacePolicy.ValidateCreate`：name 非空、≤128、不含 `:`；origin 必填；repo 必须声明在 Project | `Workspace/Domain/WorkspacePolicy.cs` |
| Store | `IWorkspaceStore.FindAsync` / `FindActiveByOriginAsync` / `ListAsync` / `InsertAsync` / `SaveAsync`；`WorkspaceRowJson.OriginKind/Payload` 已支持 slack/web 序列化 | `Infrastructure/Data/Workspace/WorkspaceStore.cs` |
| DB 约束 | PK=`(ProjectId,Name)`；**partial unique index** `(ProjectId,OriginKind,OriginPayloadJson) WHERE Status='active'` | `Migrations/20260818000000_AddWorkspace.cs` |
| 事件 | `workspace.created` / `workspace.archived` 已发，lineage 已含 origin | `WorkspaceGrain.EmitCreatedAsync/EmitArchivedAsync` |
| 回收守卫 | `WorkspaceQuerier.CountActiveBoundSessionsAsync` 驱动 close/repo-change 拒绝 | `Workspace/Services/WorkspaceQuerier.cs` |
| Session 绑定 | `AgentSession` 以 label `mohist.io/workspace-name` 持 WorkspaceName；`LabelWorkspaceName` 为 stored computed column + 索引 | `Sessions/Domain/AgentSession.cs:299`、迁移同上 |

### 1.2 issue origin 实装模式（slack/web 要照抄）

`Issue/Grains/IssueGrain.cs::StartWorkflowAsync`（≈L227）：
```csharp
var workspaceName = $"issue-{_issue!.Number}";
var wsGrain = GrainFactory.GetGrain<IWorkspaceGrain>(GrainKey.Workspace(projectId, workspaceName));
await wsGrain.EnsureIssueWorkspaceAsync(_issue.Number, repo.Name, _timeProvider.GetUtcNow());
```
- **关键**：issue 的 name 在调 grain 前就已知（`issue-<n>`），且天然 Project 内唯一（PK 兜底）。grain key = `{projectId}:{name}`，OnActivateAsync 按 key 加载 state。
- 终态归档：`IssueGrain`（≈L726）→ `wsGrain.ArchiveByIssueAsync(n, now)`，幂等、校验 origin 匹配、置 archived、发事件。

> **slack/web 不能直接照抄 grain-key 模式**：它们的 name 从 origin 派生，但「归档后重建」时基础名被归档行占住（PK 冲突）。需要应用层先做 origin→name 解析 + 唯一化，再按 name 寻址 grain。详见 §2.2。

### 1.3 Slack agent connection 入站链路（channel/DM @mention → agent 工作）

```
HTTP POST /api/projects/{p}/slack/connections/{c}/ingress   (SlackConnectionRoutes)
  → SlackIngressAsync
  → HandleChannelIngressAsync  (channel 根提及/线程)
       → LaunchChannelRootAsync
            → req.Launcher.LaunchConnectionAsync(agent, prompt, ConnectionLaunchOrigin(...), ...)
  → HandleDmIngressAsync        (DM)
       → req.Launcher.LaunchConnectionAsync(...)
```
- **`ConnectionLaunchOrigin(ConnectionId, WorkspaceTeamId, SlackUserId, ConversationId, MessageTs, ThreadTs?)`** —— 已携带 `teamId` 与 `channelId`（即 `ConversationId`）。`Agent/Services/IAgentLauncher.cs:236`。
- **`AgentLauncher.LaunchConnectionAsync`（`Agent/Services/AgentLauncher.cs:397`）硬编码 `WorkspaceName: null`** 传给 coordinator command —— **这是 slack 侧核心插入点**。
- **channel→Project 归属**：Connection → `AgentId` → `Agent.ProjectId`。入口已解析 `agent = req.Agents.GetByIdAsync(projectId, connection.AgentId)`。故 workspace 归属 = **被触发 Agent 的 Project**。同一 channel 两个不同 Project 的 Agent = 两个 Connection，各自在自己 Project 解析 → 独立 workspace（验收 4 天然成立）。
- DM 与 channel **同一 `LaunchConnectionAsync` 出口**，同一 workspace 修复同时覆盖 DM（DM 即 im-channel，`Origin={slack,teamId,imChannelId}`）。
- 既有支撑件（无需改）：`SlackThreadSessionMappingStore`（thread→session 路由）、`SlackThreadLaunchReservationStore`（并发 launch 竞态）、`SlackProviderInboxStore`（去重/容量）、`SlackStatusProjection`（liveness）。

### 1.4 Web 对话入站链路

```
HTTP POST /api/projects/{p}/agents/{agentRef}/sessions   (AgentSessionLaunchRoutes)
  → launcher.LaunchIdempotentAsync(..., AgentLaunchContext{ WorkspaceName = body.Context?.Workspace })
```
- Web 客户端 `launchAgentSession(projectId, agentRef, {prompt, context, attachments}, idempotencyKey)`：`AgentSessionLaunchContext = {issueNumber, epicNumber, repository, workspacePath}` —— **无 conversationId 字段**（`web/src/entities/agent/api/agent-sessions.ts`）。
- **Web 当前无 conversation 概念**：每次 launch = 新 session（每 launch 一个 `crypto.randomUUID()` idempotencyKey）；followup 走 `POST /agent-sessions/{sessionId}/followup` 复用同一 sessionId。即 **web「对话」≈ AgentSession**。
- 服务端 `AgentSessionLaunchContextRef(IssueNumber, EpicNumber, Repository, Workspace, WorkspacePath)`，无 conversationId。
- 路由在 launch 前已 pre-mint `sessionId`（`AgentSessionLaunchRoutes.cs:205`），可作 conversation 稳定身份。

### 1.5 root session 的 workspace 绑定与亲和（已部分落地）

- 绑定：launcher 把 `context.WorkspaceName` → coordinator command → session label `mohist.io/workspace-name`（`AgentLaunchCoordinatorGrain.cs:514`）+ AgentJob `Input.WorkspaceName`。
- 亲和：`AgentJobGrain`（≈L1014）用 `State.Input.WorkspaceName` → `WorkspaceGrain.GetHomeAsync()` → 优先路由到 home runner；**仅解析不创建**——workspace 必须在 launch 前被 ensure。
- 物化：runner 首次 dispatch 时 `WorkspaceGrain.EnsureMaterializedOnAsync(runnerId, path, now)`。

### 1.6 子 session（delegate / spawn）继承 —— **存在缺口**

- `AgentSessionSpawnRoutes` 显式拒绝 caller 传 `workspace`（`workspace_mode_retired: child sessions always inherit the parent workdir`）。
- `AgentLauncher.LaunchSubagentAsync`（`AgentLauncher.cs` L660 段）构建：
  ```csharp
  new AgentLaunchContext(projectId, WorkspacePath: admission.WorkDir)   // 只继承 workdir 路径，无 WorkspaceName
  ```
- **缺口**：子 session 不带父的 WorkspaceName 标签 → 子 AgentJob 不建立 workspace 亲和（不会路由到 home runner），且读侧查不到归属。spec 要求「委托产生的子 session 继承父 session 的 workspace」，需补：把父 session 的 WorkspaceName 透传到子 AgentLaunchContext.WorkspaceName。

### 1.7 既有 CLI 命令面（无需新增）

`mo workspace create/close/repo add/repo remove/list/view` 已实装（`cli/Mohist.Cli/MohistCliCommands.Workspace.cs`）。slack channel 归档、web 对话均**不新增 CLI**；归档走入口事件，close 仍受理 manual/交互 workspace。

### 1.8 测试基建

- Spec 轨：`Mohist.SpecTests`，`[Collection("MohistIntegration")]` + `MohistIntegrationFixture`（真 Orleans + SQLite + **`_fixture.TimeProvider` 可注入固定时间** + HTTP Client）。既有 `Workspace/WorkspaceGrainSpecs.cs`、`Workspace/IssueWorkspaceLifecycleSpecs.cs`、`Slack/SlackChannelThreadIngressSpecs.cs`（HTTP 级 fake adapter ingress，含 lease/owner 校验，断言 session provenance/inbox/outbox）。
- Unit 轨：`Mohist.UnitTests`（纯逻辑，如 `SlackConnectionRoutesNewTaskMarkerTests`）。
- slack ingress 全程 fake（`SlackRuntimeLeaseTestSupport`、假 lease、不触真实 Slack API）；时间走 `TimeProvider`。满足 testing.md 硬约束。

---

## 2. 设计

### 2.1 总体策略：入口经 Origin 解析 → ensure active workspace → 绑定 root session；子 session 继承

```
入口上下文(Slack channel/DM | Web conversation)
   │  已知 Origin = {slack,teamId,channelId} | {web,conversationId}，已知 ProjectId
   ▼
InteractionWorkspaceProvisioner.Ensure*WorkspaceAsync(projectId, origin, now)   [新应用服务]
   │  1) FindActiveByOrigin → 命中则返回其 Name（幂等复用）
   │  2) 未命中 → 派生唯一 Name → WorkspaceGrain.CreateAsync(name, origin, [], now)
   ▼  返回 WorkspaceName
Launcher（传 WorkspaceName）→ session label + AgentJob 亲和
   │
   ▼  delegate
LaunchSubagentAsync（读父 WorkspaceName → 透传子 AgentLaunchContext.WorkspaceName）
```

### 2.2 Name 派生 + Project 内唯一化

- **基础名**：
  - slack：`slack-{channelId}`（入口 payload 仅含 channelId，无 channel 显示名；人类可读名需 adapter 增强，见 D3）。
  - web：`web-{conversationId}`（conversationId = pre-minted sessionId，见 §2.5）。
- **唯一化**（解决「归档后基础名被 PK 占住」）：
  1. `Ensure*` 先 `FindActiveByOrigin`；命中即返回（同一 active workspace 复用，**不重建**）。
  2. 未命中才创建：候选 = 基础名；若 `FindAsync(projectId, candidate)` 已存在（必为 archived 行，因 active origin 未命中），递增后缀 `slack-{channelId}-2`、`-3`……直到 `FindAsync` 为空。
  3. 调 grain `CreateAsync(candidate, origin, [], now)`；grain 内再 `FindActiveByOrigin` 复检 + Insert。
- **并发兜底**：两路同时首建同一 channel → 派生同名 → 第二路 Insert 撞 PK/active-origin unique index → 抛 `workspace_conflict`；`Ensure*` 捕获后重试 `FindActiveByOrigin`（此时已能命中胜者）→ 返回胜者 Name。**active-origin partial unique index 是硬backstop**，与 issue/manual 路径一致。
- 语义保证：
  - 同一 channel 同一 active 周期 → 同一 Name（幂等）。
  - 归档后下一条触发 → 新 active workspace，Name 必不同（后缀递增）→ 验收 3 的「全新 workspace」可断言 `W1.Name != W2.Name`。
  - 跨 Project 同 channel → 各自 Project 内独立行，Name 可同（PK 含 ProjectId，不冲突）→ 验收 4。

### 2.3 Grain 改动（ generalize 现有 create + 新增 origin-archive）

现有 `CreateManualAsync` 内部逻辑（validate + FindActiveByOrigin + Insert + EmitCreated）提取为通用私有 `CreateAsync(name, origin, repos, now)`，供 manual / slack / web 共用。新增公开：

```csharp
// IWorkspaceGrain.cs
Task<WorkspaceState> CreateAsync(string name, WorkspaceOrigin origin, IReadOnlyList<string> repositoryNames, DateTimeOffset now);

// 通用 origin 归档（供 slack channel / web conversation 归档复用；issue 仍走 ArchiveByIssueAsync）
Task ArchiveByOriginAsync(WorkspaceOrigin origin, DateTimeOffset now);
```

- `CreateAsync`：与现 `CreateManualAsync` 等价但 origin 由调用方给；name/origin/repos 走 `WorkspacePolicy.ValidateCreate`；FindActiveByOrigin 复检；Insert；EmitCreated。
- `ArchiveByOriginAsync(origin, now)`：
  - 加载态为空或已 archived → 幂等返回。
  - 校验 `_state.Origin` 与传入 origin 结构相等（kind + payload），不等抛 `workspace_origin_mismatch`。
  - **不强制「无活跃 session」守卫**：channel/conversation 归档是**外部生命周期事件**（场所消亡），不同于用户 `mo workspace close`。活跃 session 自然随场所消失而结束（spec：归档后禁止新绑定，Runner 获回收授权）。**与 `CloseAsync`（强制无活跃 session + 拒 issue origin）刻意分离**。
  - 置 archived + ArchivedAt + Save + EmitArchived。Status 转 archived 即自动从 active-origin partial unique index 释放（Origin 占用解除）。

> 备选：保留 `CreateManualAsync` 公开签名不变（内部委托 `CreateAsync`），避免破坏既有 manual 调用方与 spec。新增 `CreateAsync` 仅服务 slack/web。

### 2.4 InteractionWorkspaceProvisioner（新应用服务，IScopedService，Workspace feature）

```csharp
namespace Mohist.Server.Workspace.Services;

public sealed class InteractionWorkspaceProvisioner(IWorkspaceStore store, IGrainFactory grains)
{
    // 返回 active workspace 的 Name；不存在则创建
    public Task<string> EnsureSlackWorkspaceAsync(string projectId, string teamId, string channelId, DateTimeOffset now);
    public Task<string> EnsureWebWorkspaceAsync(string projectId, string conversationId, DateTimeOffset now);

    // channel/conversation 归档：归档对应 active workspace（若存在），返回是否实际归档
    public Task<bool> ArchiveSlackChannelAsync(string projectId, string teamId, string channelId, DateTimeOffset now);
    public Task<bool> ArchiveWebConversationAsync(string projectId, string conversationId, DateTimeOffset now);
}
```
- `Ensure*`：FindActiveByOrigin → 命中返回 Name；否则 `DeriveUniqueName` → `grains.GetGrain<IWorkspaceGrain>(GrainKey.Workspace(projectId, name)).CreateAsync(name, origin, [], now)`；捕 `workspace_conflict` → 重试 FindActiveByOrigin 一次。
- `DeriveUniqueName(projectId, baseName)`：`store.FindAsync` 探测，命中则 `-N` 递增（N 从 2 起）。
- `Archive*`：FindActiveByOrigin → 命中则按其 Name 寻址 grain `.ArchiveByOriginAsync(origin, now)`；未命中返回 false（幂等）。
- 初始 `RepositoryNames = []`（交互路径「空目录 + 仓库访问权」，按需 `mo workspace repo add`；见 D4）。

### 2.5 root session 经 Origin 解析绑定

- **Slack**：`LaunchChannelRootAsync` / `HandleDmIngressAsync` 在调 `LaunchConnectionAsync` 前，先经 provisioner 解析：
  ```csharp
  var workspaceName = await req.Services.GetRequiredService<InteractionWorkspaceProvisioner>()
      .EnsureSlackWorkspaceAsync(projectId, body.TeamId, body.ConversationId, req.TimeProvider.GetUtcNow());
  ```
  并给 `IAgentLauncher.LaunchConnectionAsync` **新增 `string? workspaceName` 参数**，透传到 coordinator command 的 `WorkspaceName`（替换现硬编码 `null`）。
- **Web**：`AgentSessionLaunchRoutes` 新建 session 分支（非 resume）：
  ```csharp
  var conversationId = preMintedSessionId;   // web 对话身份 = sessionId（见 D2）
  var workspaceName = await provisioner.EnsureWebWorkspaceAsync(project.Id, conversationId, now);
  var launchContext = new AgentLaunchContext(project.Id, WorkspaceName: workspaceName, ...);
  ```
  resume 分支不重建（session 已绑 workspace）。

### 2.6 子 session 继承

- `AgentSpawnAdmission`（`Agent/Services/AgentSpawnAdmissionService.cs::AdmitAsync`）在解析父 session 时**读父 session 的 WorkspaceName label**，加入 admission。
- `AgentLauncher.LaunchSubagentAsync` 用 admission 的父 WorkspaceName 填子 `AgentLaunchContext.WorkspaceName`：
  ```csharp
  new AgentLaunchContext(projectId, WorkspaceName: admission.ParentWorkspaceName, WorkspacePath: admission.WorkDir)
  ```
- 效果：子 session 带 workspace name 标签 → 子 AgentJob 建立 home-runner 亲和 → 读侧可查归属。workdir 路径继承不变。

### 2.7 Slack channel 归档 → workspace 归档接线（**scope 边界见下**）

- **Server 侧归档路径（本 issue 范围内）**：`InteractionWorkspaceProvisioner.ArchiveSlackChannelAsync` + 一个**入口 hook**（Server 内可达的调用点），由「channel 已归档」的外部信号触发，归档该 (project,teamId,channelId) 的 active slack workspace、释放 Origin。
- **scope 边界（需主 agent 裁决 D1）**：
  - issue Non-Goals 原文：「Slack channel 归档事件接线的**具体事件源适配**之外的**通用入口框架**」。
  - 解读 A（推荐，满足验收 3）：**本 issue 做「通用入口框架」= Server 侧归档动作 + 入口 hook + 可被 fake 驱动**；**具体 slack archive 事件源适配**（`mohist-slack` adapter 订阅 Slack `channel.archive`/`group.archive`/`im.close` 事件并翻译为 Server hook）列为 **non-goal / 跟进 issue**。验收 3 通过 fake 驱动 Server 侧归档 hook 满足，不依赖真实 Slack。
  - 解读 B（更窄）：连 Server 入口 hook 也不做，验收 3 无法满足 → 与验收 3 矛盾，**不采纳**。
- 入口 hook 形态（草案，二选一，D1 定）：
  - (a) 复用 slack ingress 路由族，新增 `POST .../connections/{c}/channel-archive`（adapter 调用，带 lease 鉴权，body=`{teamId, conversationId}`）。
  - (b) 纯内部 service 调用（adapter 经既有 lease 通道发一个 envelope，Server handler 调 provisioner）。
- **DM**：DM 无「归档」语义，不接归档；web 同理（见 §2.8）。

### 2.8 Web conversation lifecycle

- Web 无外部归档事件；web workspace 仅由 `mo workspace close` 显式归档（既有能力）。conversationId=sessionId，一个对话一个 workspace，持久累积；conversation 内 followup/subagent 复用同一 workspace。
- `InteractionWorkspaceProvisioner.ArchiveWebConversationAsync` 仍提供（对称 + 供未来「关闭对话」入口 / 测试），但本 issue **不接自动归档触发源**。

---

## 3. 逐项改动清单（文件级 + 接口签名草案）

### 3.1 Server（核心：grain / store / provisioner / launcher / 子继承 / DI）

| # | 文件 | 改动 |
|---|---|---|
| S1 | `Workspace/Grains/IWorkspaceGrain.cs` | 新增 `Task<WorkspaceState> CreateAsync(string name, WorkspaceOrigin origin, IReadOnlyList<string> repositoryNames, DateTimeOffset now);` 与 `Task ArchiveByOriginAsync(WorkspaceOrigin origin, DateTimeOffset now);` |
| S2 | `Workspace/Grains/WorkspaceGrain.cs` | 提取私有 `CreateAsync`（现 `CreateManualAsync` 委托之）；实装公开 `CreateAsync`；实装 `ArchiveByOriginAsync`（校验 origin 匹配、**不查活跃 session**、置 archived、EmitArchived）。`ArchiveByIssueAsync` 可保留或改为委托 `ArchiveByOriginAsync(new Issue(n), now)`。 |
| S3 | `Infrastructure/Data/Workspace/WorkspaceStore.cs` | （可选）新增 `Task<bool> NameExistsAsync(string projectId, string name, ct)` 供 provisioner 探测；或复用 `FindAsync`。**无 schema 变更**（slack/web origin 的 kind/payload 序列化与索引已就绪）。 |
| S4 | `Workspace/Services/InteractionWorkspaceProvisioner.cs` | **新文件**。`EnsureSlackWorkspaceAsync` / `EnsureWebWorkspaceAsync` / `ArchiveSlackChannelAsync` / `ArchiveWebConversationAsync` + 私有 `DeriveUniqueName`。依赖 `IWorkspaceStore` + `IGrainFactory`。 |
| S5 | `Agent/Services/IAgentLauncher.cs` | `LaunchConnectionAsync` 签名**新增** `string? workspaceName = null`（向后兼容默认 null）。 |
| S6 | `Agent/Services/AgentLauncher.cs::LaunchConnectionAsync`（L397） | `WorkspaceName: workspaceName`（替换硬编码 `null`）。 |
| S7 | `Agent/Services/AgentLauncher.cs::LaunchSubagentAsync` | 子 `AgentLaunchContext` 填 `WorkspaceName: admission.ParentWorkspaceName`。 |
| S8 | `Agent/Services/AgentSpawnAdmissionService.cs` | `AdmitAsync` 解析父 session 时读其 WorkspaceName label，置入 `AgentSpawnAdmission.ParentWorkspaceName`；`AgentSpawnAdmission` record 加该字段。 |
| S9 | `Infrastructure/Hosting/MohistServiceRegistration.cs`（或对应 DI 入口） | 注册 `InteractionWorkspaceProvisioner`（IScopedService）。 |
| S10 | `Workspace/Domain/WorkspacePolicy.cs` | （大概率无需改）确认 `ValidateCreate` 对 `Origin.Slack/Web` 通过；如需对 teamId/channelId/conversationId 非空校验，在此加。 |

> 无 DB 迁移：slack/web 的 OriginKind/OriginPayloadJson 列与 partial unique index 已存在；session 的 `LabelWorkspaceName` 已存在。**零 schema 变更**是本计划的重要属性。

### 3.2 Slack 入站接线

| # | 文件 | 改动 |
|---|---|---|
| K1 | `Api/SlackConnectionRoutes.cs::LaunchChannelRootAsync` | 调 `LaunchConnectionAsync` 前 `EnsureSlackWorkspaceAsync(projectId, body.TeamId, body.ConversationId, now)`；把返回 Name 传入新增的 `workspaceName` 参数。时间取 `req.TimeProvider`（确认 req 是否已持；未持则注入）。 |
| K2 | `Api/SlackConnectionRoutes.cs::HandleDmIngressAsync` | 同 K1（DM 即 im-channel，同 Origin 形态）。 |
| K3 | `Api/SlackConnectionRoutes.cs`（或新 `SlackChannelArchiveRoutes`） | **入口 hook**（D1 定形态）：鉴权后调 `provisioner.ArchiveSlackChannelAsync(projectId, teamId, channelId, now)`。仅 Server 侧动作。 |
| K4 | `packages/mohist-slack/**` | **non-goal（D1）**：adapter 订阅 Slack channel/group/im 归档事件 → 调 K3 hook。本 issue 不做，留跟进 issue。 |

### 3.3 Web 入站接线

| # | 文件 | 改动 |
|---|---|---|
| W1 | `Api/AgentSessionLaunchRoutes.cs`（新建 session 分支，≈L205-253） | pre-mint `sessionId` 后 `EnsureWebWorkspaceAsync(project.Id, sessionId, now)`；`AgentLaunchContext.WorkspaceName = 结果`（覆盖原 `body.Context?.Workspace`）。resume 分支不动。注入 `InteractionWorkspaceProvisioner` 到 handler 参数。 |
| W2 | `web/src/entities/agent/api/agent-sessions.ts` | **可选**：`AgentSessionLaunchContext` 注释说明 web 对话 workspace 由 server 自动解析，`workspacePath` 不再驱动 workspace 绑定。**无契约破坏**（context 字段不变）。 |
| W3 | `web/src/pages/agent-session-composer/ui/AgentSessionComposerPage.tsx` | **可选**：移除/保留 `workspace` context chip（当前发的是 `workspacePath`，与 server `Workspace` 字段本就错位；自动解析后该 chip 失效，可后续清理）。本 issue 不强制改 Web UI。 |

---

## 4. 测试计划

### 4.1 硬约束（testing.md）
- 无真实 Slack / Web / 网络 / 进程 / git / DB 外部服务：全程 `MohistIntegrationFixture`（SQLite + 真 Orleans + fake slack adapter + lease）。
- 无真实时间：`_fixture.TimeProvider` 注入固定时间。
- slack/web ingress 用 fake（既有 `SlackRuntimeLeaseTestSupport` + HTTP ingress）。
- 不 flaky：并发用确定性 grain 调用，不靠时序。

### 4.2 Spec 轨（覆盖验收 1–4 + 归档/继承）—— 新增 `Specs/Workspace/InteractionWorkspaceSpecs.cs` 或拆 slack/web 两文件

| 用例 | 对应验收 | 断言要点 |
|---|---|---|
| `SlackChannel_FirstTrigger_CreatesWorkspaceAndBindsSession` | 基线 | 首条 channel root mention → workspace W(active, Origin=slack(teamId,channelId)) 创建；session label WorkspaceName=W.Name；`workspace.created` 事件含 slack origin。 |
| `SlackChannel_SecondSession_ReusesSameWorkspace`（验收 1） | 验收 1 | 同 channel 第二次触发（新 session）→ **复用** W（Name 不变，无第二条 created 事件）；两 session 的 WorkspaceName 相等 → 互相可见同一目录（home runner 亲和一致）。 |
| `SlackChannel_SecondAgent_EntersSameWorkspace`（验收 2） | 验收 2 | 同 channel 拉入另一 Agent（另一 Connection，**同 Project**）→ 其 session 绑定**同一** W.Name（FindActiveByOrigin 命中）。 |
| `SlackChannel_Archive_Then_NextTrigger_CreatesFreshWorkspace`（验收 3） | 验收 3 | 触发 → W1；驱动归档 hook → W1.Status=archived、Origin 释放、`workspace.archived` 事件；再触发 → W2(active)、**W1.Name != W2.Name**（后缀递增）、W2 Origin=同 (teamId,channelId)；W2 session 绑 W2.Name。 |
| `SlackChannel_TwoProjects_HaveIndependentWorkspaces`（验收 4） | 验收 4 | 同 (teamId,channelId)，两不同 Project 的 Connection 各 ensure → 两 active workspace（不同 ProjectId、可同名），互不影响。 |
| `SlackDM_FirstTrigger_CreatesWorkspaceWithImChannelOrigin` | DM 覆盖 | DM 触发 → workspace Origin=slack(teamId, imChannelId)；同 DM 后续 followup 复用。 |
| `SubagentChild_InheritsParentWorkspaceName`（继承） | spec | slack/web 触发后 spawn 子 session → 子 session label WorkspaceName == 父；子 AgentJob `Input.WorkspaceName` 非空（建亲和）。 |
| `WebConversation_FirstLaunch_CreatesWorkspace` | web 基线 | web launch → workspace Origin=web(sessionId)；同 conversation followup 复用同 session 同 workspace。 |
| `WebConversation_NewConversation_GetsNewWorkspace` | web | 新 launch（新 sessionId）→ 新 workspace（不同 conversationId）。 |
| `SlackChannel_ConcurrentFirstCreate_ResolvesToOneWorkspace` | 并发 | 两路并发首建同 channel → 恰好一个 active workspace（PK/unique index 兜底，败者重试命中胜者）；无重复 created 事件。 |
| `SlackChannel_Archive_IdempotentAndNoSessionGuard` | 归档语义 | 归档 hook 重放幂等；归档**不**因有活跃 session 而拒绝（区别于 `mo workspace close`）。 |

> 复用 `SlackChannelThreadIngressSpecs` 的 `CreateConnectionAsync` / lease / HTTP ingress helper；归档 hook 用 fake 直接调 provisioner 或 POST K3 路由（带 lease）。

### 4.3 Unit 轨（纯逻辑）—— `Mohist.UnitTests/Workspace/`

- `InteractionWorkspaceProvisionerTests`：`DeriveUniqueName` 后缀递增（mock store：基础名已被 archived 行占 → 返回 `-2`）；`EnsureSlack` 命中 active 即不创建；`ArchiveSlack` 未命中返回 false。
- `WorkspacePolicyTests`（如 S10 加校验）：slack/web origin 字段非空校验。
- 既有 `WorkspaceGrainSpecs`/`IssueWorkspaceLifecycleSpecs` 回归不破（`CreateManualAsync` 委托 `CreateAsync` 后行为不变）。

### 4.4 Architecture 测试
- `InteractionWorkspaceProvisioner` 属 Workspace feature，不反向依赖 Slack/Agent 域（仅依赖 store + grain factory + domain origin 类型）。
- slack/web ingress 调 provisioner 经 DI（不直接 new），方向：`Api/Slack*`、`Api/AgentSessionLaunch*` → `Workspace.Services`（正向）。

---

## 5. 风险与兼容

| 风险 | 评估 | 缓解 |
|---|---|---|
| **零 schema 变更** 是否漏了 session workspace 标签索引 | 已存在（`LabelWorkspaceName` stored computed + index，迁移 `20260818`） | 无需迁移，回归既有 spec |
| `CreateManualAsync` 重构破坏 manual 路径 | 低（纯提取） | 保留公开签名；`WorkspaceGrainSpecs`/`Api/WorkspaceSpecs` 回归 |
| 归档不查活跃 session 与 `CloseAsync` 语义混淆 | 中 | 文档/错误码区分：`ArchiveByOriginAsync`（外部事件，无守卫）vs `CloseAsync`（用户 close，有守卫 + 拒 issue）；spec 已述 |
| Name 后缀递增在极端重放下无限增长 | 低 | channelId/conversationId 稳定，重建频次低；可接受；不做复杂回收 |
| web conversationId=sessionId 导致「每 launch 必建 workspace 实体」 | 中（语义） | 符合 spec「一个对话一个 workspace」；如需复用见 D2 |
| slack channel 名不可读（`slack-{channelId}`） | 低（体验） | 见 D3，后续 adapter 传 channel 显示名时升级派生 |
| `LaunchConnectionAsync` 加参破坏既有调用方 | 低（默认 null） | 仅 slack ingress 传值；其它调用方（如有）保持 null 行为 |
| 子继承改 `AgentSpawnAdmission` shape | 低 | record 加字段，既有 spawn spec 回归 |
| 并发首建竞态 | 低 | PK + active-origin unique index 兜底 + 败者重试，与 issue 路径同构 |

---

## 6. 需主 agent 决策的问题

- **D0（编号）**：实时 #330 是已完成的 session 重构；本功能在 tracker 的真实编号待定。是否需主 agent 在 tracker 修正/新建 issue 并对齐工作区名？（不阻塞本计划落地，但影响 PR/issue 关联。）
- **D1（slack channel 归档事件源 scope —— prompt 明确要求裁决）**：采用解读 A（**本 issue = Server 侧归档动作 + 入口 hook + fake 可驱动；adapter 订阅真实 Slack archive 事件 = non-goal / 跟进 issue**）？入口 hook 选 (a) HTTP 路由 还是 (b) 内部 envelope handler？**推荐 A + (a)**，使验收 3 可在无真实 Slack 下满足。
- **D2（web conversationId 身份）**：采用 **conversationId = pre-minted sessionId**（最小、忠于现有 web 模型，一个对话=一个 session=一个 workspace）？还是引入 web 客户端拥有的稳定 conversation UUID（支持跨 session 复用、更大 web 改动）？**推荐前者**；后者列为后续。
- **D3（slack workspace Name 可读性）**：首版用 `slack-{channelId}`（入口仅含 channelId，无额外 API）；是否在本 issue 顺带让 ingress payload 携带 channel 显示名以派生人类可读名？**推荐不在本 issue**（避免改 adapter ingress 契约），留跟进。
- **D4（交互 workspace 初始 RepositoryNames）**：首版 `[]`（空目录 + 按需 `repo add`），还是预填 Project 全部仓库？**推荐 `[]`**（最小授权原则；agent/owner 按需挂载）。
- **D5（web client `context.workspace`/`workspacePath` 去留）**：自动解析后该字段失效（且现状 client 发 `workspacePath` 与 server 读 `Workspace` 本就错位）。本 issue 是否顺手清理 web composer 的 workspace chip？**推荐留清理到后续**，本 issue 仅 server 自动解析。

---

## 7. 可委派子任务拆分

> 遵循 `design/dispatch-template.md` 三条硬规则（model fallback 链 + 探活、测试命令 timeout、完成定义=build+测试+PR）。每子任务独立交付价值一句话可说清，文件重叠最小化，建议**串行 stack**（S 组是 K/W 组的前置）。

### 子任务 A — Server 核心：Workspace origin 创建/归档 + provisioner（S1–S4, S9, S10）
- 交付：grain `CreateAsync`/`ArchiveByOriginAsync`、`InteractionWorkspaceProvisioner`、DI 注册、policy 校验（如需）。
- 测试：`WorkspaceGrainSpecs` 加 slack/web create/archive 用例；unit `InteractionWorkspaceProvisionerTests`（name 唯一化、幂等、并发重试）。
- 完成定义：`npm test`（server）绿 + unit 绿 + PR。
- 无前置依赖；**是 B/C 的硬前置**。

### 子任务 B — Slack 入口接线 + 归档 hook（K1–K3, S5, S6）
- 交付：`LaunchConnectionAsync` 加 `workspaceName` 参；channel/DM ingress 解析绑定；channel-archive 入口 hook（依 D1）。
- 测试：`InteractionWorkspaceSpecs`（slack）覆盖验收 1–4 + DM + 并发 + 归档。
- 前置：A 合入。
- 完成定义：server spec/unit 绿 + PR（基于 A）。

### 子任务 C — Web 入口接线（W1）
- 交付：`AgentSessionLaunchRoutes` 新建分支 web workspace 自动解析（conversationId=sessionId，依 D2）。
- 测试：`InteractionWorkspaceSpecs`（web）+ web unit/dom 回归。
- 前置：A 合入；可与 B 并行（不同文件）。
- 完成定义：server spec 绿 + PR。

### 子任务 D — 子 session workspace 继承（S7, S8）
- 交付：`AgentSpawnAdmission` 携父 WorkspaceName；`LaunchSubagentAsync` 透传。
- 测试：`AgentSubagentLaunchSpecs` 加「子继承父 workspace name」用例。
- 前置：A 合入；可与 B/C 并行。
- 完成定义：spawn spec 绿 + PR。

### 子任务 E（跟进，**非本 issue**）— Slack archive 事件源适配（K4）+ web 可读性/清理（D3, D5）
- adapter 订阅 Slack `channel.archive`/`group.archive` → 调 K3 hook；web composer workspace chip 清理；可选 channel 显示名派生。**单列跟进 issue，不进 #330 验收**。

---

## 附：关键代码定位速查

| 关注点 | 位置 |
|---|---|
| Workspace 模型 / Origin 四分支 | `packages/server/src/Mohist.Server/Workspace/Domain/Workspace.cs` |
| Grain create/archive（含 slack/web 待补） | `Workspace/Grains/WorkspaceGrain.cs` |
| issue origin 调用范式 | `Issue/Grains/IssueGrain.cs:227` / `:726` |
| Store + Origin 序列化（已支持 slack/web） | `Infrastructure/Data/Workspace/WorkspaceStore.cs` |
| DB 约束（PK + partial unique origin index） | `Infrastructure/Data/Migrations/20260818000000_AddWorkspace.cs` |
| Slack channel/DM 入口 | `Api/SlackConnectionRoutes.cs::LaunchChannelRootAsync` / `HandleDmIngressAsync` |
| Slack 连接 launch（硬编码 null） | `Agent/Services/AgentLauncher.cs:397` |
| ConnectionLaunchOrigin（带 teamId/channelId） | `Agent/Services/IAgentLauncher.cs:236` |
| Web launch 入口 | `Api/AgentSessionLaunchRoutes.cs:205/247` |
| Web 客户端 launch 契约 | `web/src/entities/agent/api/agent-sessions.ts` |
| Session workspace label 键 | `Sessions/Domain/AgentSession.cs:299` |
| 子 session spawn（继承缺口） | `Agent/Services/AgentLauncher.cs::LaunchSubagentAsync` / `AgentSessionSpawnRoutes.cs` |
| AgentJob workspace 亲和（只解析不创建） | `Agent/Grains/AgentJobGrain.cs:1014` |
| 既有 spec harness | `SpecTests/Specs/Workspace/*`、`SpecTests/Specs/Slack/SlackChannelThreadIngressSpecs.cs` |
