# SINK-PLAN-290 — IntegrationSessions / IntegrationIssue / IssueLifecycle 族集成测试下沉方案

> **状态**：设计阶段产出（#290）。本文是 plan，不改动任何测试或产品代码。
> 实现由后续 agent 按 §7 迁移清单分批执行。
>
> **依据**：`design/testing.md`「The lowest useful layer owns the behavior matrix. API/integration
> specs assert route, binding, status code, JSON shape, parameter parsing, and one success path per
> endpoint; state and calculation permutations belong to the querier/grain/domain specs below.」
>
> 本 plan 是 **#290 子 issue** 的设计（族 = Session/Issue/IssueLifecycle，约 335 个全栈测试），
> 与 #289 兄弟 issue 的 [SINK-PLAN-289.md](./SINK-PLAN-289.md) 模板/口径一致（fixture 清单、
> Keep/Sink 拆分判据、迁移分批、CI 节省估算）。两 plan 互不重叠（#289 覆盖 IntegrationApi /
> IntegrationTelemetry / IntegrationMisc / IssueProfile；本 plan 覆盖 IntegrationSessions /
> IntegrationIssue / IssueLifecycle），但共享 `MohistIntegrationFixture` 资源，所以执行顺序需
> 协调（见 §8）。

## 1. 目标与范围

把三个走全栈 HTTP（每集合一个 `MohistIntegrationFixture`：独立 silo + WebApplicationFactory + EF
+ SQLite）的 spec 集合里的**状态/计算排列**下沉到无 web host 的轻量 fixture
（`MohistDbFixture` 生产 DI 图 + 内存 SQLite，无 silo 无 host；`WorkflowGrainFixture` /
`AgentSessionGrainFixture` 用 `InProcessTestCluster` 内存传输无端口），把 API 层只留**契约断言**
（路由 / 绑定 / 状态码 / JSON 形状 / 参数解析 / 每端点一条成功路径）。

审计范围（共 35 文件 / 335 测试）：

| 集合 (xUnit Collection) | 共享 Fixture | 文件数 | 测试数 |
|---|---|---|---|
| `IntegrationSessions` | `MohistIntegrationFixture`（silo + web host） | 17 | 152 |
| `IntegrationIssue` | `MohistIntegrationFixture` | 9 | 119 |
| `IssueLifecycle` | `MohistIntegrationFixture` | 9 | 64 |
| **合计** | — | **35** | **335** |

> 注：#290 issue 文本给的口径是"约 377 个全栈测试"；本 plan 审计实测是 335，差额来自
> - 之前 #289 PR 落地时已把 `IssueProfile` 的部分 issue-sink spec 移走（19 → 11 文件；
>   11 个沉到 querier/grain 后本 plan 看不见）；
> - 部分 Session API spec（`GenericAgentSessionSummarySpecs` / `UnifiedSessionRoutesSpecs` /
>   `UnifiedSessionSummarySpecs`，共 51 测试）已**不在** `IntegrationSessions` 集合，迁移到了
>   `MohistDbFixture`/`UnifiedSessionSummaryFactory` 的轻 fixture —— 本 plan 不重复下沉。
> - 部分同名集合的 grain/spec 文件名变更。本 plan 以来源 `git grep` 实测为准（335），与
>   SINK-PLAN-289 §1 同样的「审计时实测为权威」原则。

`MohistIntegrationFixture` 是 CI 关键路径主体（每测试 50–150ms+ 的 HTTP 往返 + DB 查询 + silo
grain 调用；CI 4-vCPU 比本地慢 ~35%）。三集合各自一个独立 fixture 实例（`TestClusterPortAllocator`
分配不同 silo/gateway 端口，可并行），但**任何**集合串行链都受 HTTP 往返 + silo 启动影响。

## 2. 分类判据（契约 vs 计算）

**留下 API 层（契约）**——断言以下之一即可，且每端点只留**一条**成功路径：
- 路由可达 / 已移除路由返回 404 / 跨 project 隔离的 404
- 请求绑定（multipart / JSON 字段缺失 / content-type）→ 400/415
- 参数解析与校验（`limit` 越界、`range` 枚举、`types` 非法、host 路由、prerequisite 缺字段）→ 400/404
- 状态码语义（201 创建、409 冲突、`agentNotFound` 404、`agentArchived` 404、`runnerOffline` 202 等）
- 响应 JSON 形状（envelope 字段、列表 vs 对象、`data`/`children` 结构、null/omit 区分、watching/muted 数组）
- 单条成功路径（端到端走通一次，包含 DTO 字段映射）

**下沉（状态/计算排列）**——以下逻辑不该在 HTTP 层重复，属于 querier/grain/domain：
- 排序 / 分页 / 默认 limit / 跨页 tie-break
- 项目隔离 / 范围过滤 / 多源合并投影
- 状态转移（start/cancel/reopen/close/archive/detach、composite 聚合、idempotency dedup）
- 计算派生量（metrics 桶粒度、window 缩放、stage duration、approval wait、quality primary/previous）
- 内容投影（issue 列表字段裁剪、label filter 命中、feedback 阶段作用域、composite child 排序）
- 调度/同步/恢复语义（recovery 端点的 idempotency key 重放、runner offline 排队、schedule dedup）
- 状态机（composite 父→子状态聚合、`startComposite` fan-out 失败隔离、`recomputeCompositeStatus`
  重投幂等）

判据一句话：**一次行为变更只能动一个测试文件**；如果改某计算逻辑会同时改 HTTP 层和下层两个文件，
HTTP 层那份就是该下沉的重复矩阵。

## 3. Fixture 盘点与能力矩阵

### 3.1 现有可下沉 fixture

| Fixture | 集合 | 能提供 | 不能提供 | 适用下沉 |
|---|---|---|---|---|
| `MohistDbFixture` (`Support/`) | `MohistDb` | 生产 DI 图（`ConfigureMohistServices`）、内存 SQLite（`MigratedSqliteTemplate.CopyTo`）、所有 `IScopedService`/`ISingletonService`（Scrutor 自动注册：querier / store / assembler / `IssueQuerier` / `IssueMetrics*Querier` / `ProjectRepositoryQuerier` 等） | **无 web host、无 Orleans silo**（`Grains` 抛 `NotSupportedException`） | querier / store / 解析服务的**纯逻辑**测试；domain 与 EF 读侧测试 |
| `WorkflowGrainFixture` (`Specs/Workflow/`) | `WorkflowGrain` | `InProcessTestCluster`（内存传输，无端口）、`IGrainFactory`、内存 SQLite、`RecordingEventStore`、`FakeTimeProvider` | 无 web host | grain 编排 / 状态转移 / 持久化 / 复合状态机 |
| `AgentSessionGrainFixture` (`Specs/Sessions/`) | （grain 内部） | `InProcessTestCluster` + `FakeAgentSessionStore`/`TranscriptStore` + `TestSqliteDatabase` + `RecordingTranscriptEventPublisher` + `RecordingFollowupDispatchScheduler` | 无 web host | AgentSession grain 行为（context association、transcript 投影、followup 调度、recovery） |

`IScopedService` 约定（`Infrastructure/Hosting/ServiceCollectionExtensions.cs`）经 Scrutor
`AddClasses(AssignableTo<IScopedService>)` 自动注册为自身 —— 这意味着
`IssueQuerier` / `IssueMetricsCompletionQuerier` / `IssueMetricsStageDurationsQuerier` /
`IssueMetricsQualityQuerier` / `IssueMetricsDeliveryTimesQuerier` /
`IssueMetricsApprovalWaitQuerier` / `IssueMetricsBucketsQuerier` / `IssueParentContextQuerier` /
`IssueListProjection` 等在 `MohistDbFixture.Services` 里都可直接 `GetRequiredService`，
**无需** web host（参照 `IssueMetricsCompletionSpecs` 现有 `MohistDb` 集合用法）。

### 3.2 Fixture 缺口（标注为风险，本 plan 不擅自补）

| 缺口 | 影响 | 处置建议 |
|---|---|---|
| **`IssueMetricsBucketsQuerier` 与 `IssueMetricsApprovalWaitQuerier` 决策点待定**（可能在 querier service / 可能在 grain 编排 / 可能横跨两者） | `IssueMetricsApiSpecs` 20 个测试里 18 个是 range/bucket 计算，需确认归属 | 实现阶段先 grep `IssueMetrics*` 路径确认 querier 与 grain 边界，再选 fixture。**该批（A 批 #4）放最后做**或独立 PR 定位。 |
| **`IssueCompositeAdvancementGrainSpecs` 跨 IssueGrain + WorkflowGrain 编排** | `StartCompositeAsync` / `RecomputeCompositeStatusAsync` 跨 grain，需要 `InProcessTestCluster` + 两个 grain 都注册 | 用 `WorkflowGrainFixture`（已含 IssueGrain + WorkflowGrain 注册）；或新建 `IssueCompositeGrainFixture`。**放 D 批**。 |
| **`AgentSessionContextAssociationApiSpecs` 的实现是否在 querier 而非 grain** | 7 个测试是 issue→sessions 关联查询，归属待定位 | 先 grep `IIssueSessionAssociationQuerier` / `IssueContextAssociationQuerier` 路径；service → `MohistDbFixture`，grain → `AgentSessionGrainFixture`。**放 D 批**。 |
| **`AgentSessionScheduleApiSpecs` 的 schedule 持久化在 grain vs store** | 13 个测试覆盖 schedule CRUD + dedup + cancel，需确认 sink 落点 | 已在 `AgentSessionGrainFixture` 内的 `FakeAgentSessionStore` 能 hold schedule 数据（待确认）；若不在则走 `MohistDbFixture` + EF store。**放 D 批**。 |
| **`IssueCompositeAdvancementApiSpecs` 的 fan-out 失败隔离** | `StartCompositeAsync` 的 fan-out 跨 `IIssueGrain.StartWorkAsync`，是 grain 编排 | 已是 grain fixture 范畴，迁到 `WorkflowGrainFixture`（含 `InProcessTestCluster`）。**放 D 批**。 |
| **`IssueFeedbackApiSpecs` 的 feedback 投影（stage scoping、open/resolved 区分）** | 17 个测试里 14 个是反馈的 stage filter / ordering / resolution projection | 落 querier / store → `MohistDbFixture`；feedback service 待定位。**放 A 批**前置 grep。 |

## 4. 服务边界图（端点 → 归属服务 → 下沉目标）

> 此表为下沉目标。Service 已存在的（`IScopedService` / `ISingletonService`）直调；未明确的
> 标注"待定位"。**实现 agent 必须先 grep 确认边界**，再选 fixture。

| 端点 | 归属服务（待定位） | 下沉 fixture |
|---|---|---|
| `POST /api/projects/{ref}/issues` + 完整 Create/Update 流程 | `Issue.CreateAsync`（grain） | `WorkflowGrainFixture`（grain） |
| `POST /api/projects/{ref}/issues` 的 prereq bind / labels / risk | issue domain + label store | `MohistDbFixture`（domain/store unit） |
| `GET /api/projects/{ref}/issues` 列表 + filter（label/labelKey/repo/status/prereq） | `IssueQuerier`（querier） | `MohistDbFixture` |
| `GET /api/projects/{ref}/issues/{n}` 详情（含 feedback / watching / children / workflow） | `IssueQuerier` + `IssueFeedbackQuerier` + `IssueWatchingQuerier` + composite child projection | `MohistDbFixture`（每段分别一个 spec） |
| `GET /api/projects/{ref}/issues/{n}/stage-state` + `feedback` | `IssueFeedbackQuerier`（stage scoping） | `MohistDbFixture` |
| `POST /api/projects/{ref}/issues/{n}/feedback` 创建/解析 | `IIssueFeedbackService` / grain 编排 | service → `MohistDbFixture`；编排 → `WorkflowGrainFixture` |
| `POST /api/projects/{ref}/issues/{n}/done`（Manual） | `IIssueGrain.DoneAsync`（grain） | `WorkflowGrainFixture` |
| `POST /api/projects/{ref}/issues/{n}/start` 含 composite advancement | `IIssueGrain.StartCompositeAsync`（grain） | `WorkflowGrainFixture` |
| `POST /api/projects/{ref}/issues/{n}/cancel` / `reopen` / `archive` / `close` 含 composite 编排 | `IIssueGrain` + composite aggregate | `WorkflowGrainFixture` |
| `POST /api/projects/{ref}/issues/{n}/watch` + `DELETE` | `IIssueWatchService` / grain | service → `MohistDbFixture`；grain → `WorkflowGrainFixture` |
| `GET /api/projects/{ref}/issues/{n}/readiness` | `IIssueGrain` + `IssueStartReadinessDomain` | `MohistDbFixture`（domain） + grain fixture（orchestration） |
| `POST /api/projects/{ref}/issues/{n}/repository` patch + 历史 repo 保留 | `IssueRepositoryBindingDomain` + grain | `MohistDbFixture`（domain） + grain fixture |
| `GET /api/projects/{ref}/issues/parent-candidates` / `unread-count`（见 #289 §4，不再重复） | `IssueQuerier` / `IssueUnreadCountQuerier` | `MohistDbFixture`（已 #289 覆盖，本 plan 不重复） |
| `GET /api/projects/{ref}/metrics/completion-day` / `-week` / `delivery-times` / `stage-durations` / `approval-wait` / `quality` / `cumulative-flow` | `IssueMetrics*Querier` 系列 + `IssueMetricsBucketsQuerier` | `MohistDbFixture`（每段一个 spec） |
| `GET /api/projects/{ref}/issues/{n}/archived-detail` 形状（`workflowRunId`/`archivedAt`） | `IssueQuerier` 投影 + `ArchivedIssueDetailProjection` | `MohistDbFixture` |
| `GET /api/projects/{ref}/issues/{n}/workflow`（lifecycle：start/cancel/complete/rerun） | `IIssueGrain` + `IWorkflowGrain`（grain orchestration） | `WorkflowGrainFixture` |
| `GET /api/projects/{ref}/issues/{n}/comments` + `POST` | `IIssueGrain.AddCommentAsync`（grain） | `WorkflowGrainFixture` |
| `PATCH /api/projects/{ref}/issues/{n}`（risk/priority/title/body/labels/raw-presence-merge） | issue domain + grain 编排 | domain → `MohistDbFixture`；grain → `WorkflowGrainFixture` |
| `GET /api/agents/{ref}/sessions`（`AgentSessionReadApi`） | `AgentSessionListQuerier` + `IssueTitleLookup` | `MohistDbFixture` |
| `GET /api/agents/{ref}/sessions/{id}/summary` / `transcript` / `history` | `AgentSessionSummaryAssembler` + `AgentSessionTranscriptProjection` | `MohistDbFixture` |
| `GET /api/issues/{n}/sessions`（context association） | `IIssueSessionAssociationQuerier`（**待定位**） | service → `MohistDbFixture`；grain → `AgentSessionGrainFixture` |
| `GET /api/agents/{ref}/sessions/{id}/schedule` + CRUD | `IAgentSessionScheduleService`（**待定位**） | `MohistDbFixture` 或 `AgentSessionGrainFixture` |
| `POST /api/agents/{ref}/sessions/{id}/recovery` / `compact` / `reset`（含 idempotency） | recovery orchestrator（grain） | `AgentSessionGrainFixture` |
| `POST /api/agents/{ref}/sessions/{id}/followup` + `cancel` | `IAgentSessionFollowupService` + `IFollowupDispatchScheduler` | service → `MohistDbFixture`；grain → `AgentSessionGrainFixture` |
| `POST /api/agents/{ref}/sessions/{id}/stop`（含 fingerprint dedup） | `ISessionStopOrchestrator` | `AgentSessionGrainFixture` |
| `GET /api/agents/{ref}/sessions/{id}/issue-workflow-history` | `IIssueWorkflowSessionHistoryQuerier` | `MohistDbFixture` |
| `GET /api/projects/{ref}/sessions/activity`（amplification） | `AgentActivityFeedAssembler` | `MohistDbFixture`（已 #289 覆盖 ActivityEvidenceAssembler 部分，sessions 侧补） |
| `GET /api/agents/{ref}/sessions/{id}/path-amplification` / `activity` | `AgentPathAmplificationQuerier` + `AgentActivityCardQuerier` | `MohistDbFixture` |
| `GET /api/issues/{n}/sessions/transcript` | `AgentSessionTranscriptProjection` | `MohistDbFixture` |
| `POST /api/issues/{n}/comments` 等 | 已在 #290 issue 边界内的"Issue 域 session 端点" | 按归属映射 |

## 5. 逐集合下沉分解

> 列说明：**Keep** = 留 API 层契约；**Sink** = 下沉计算；**Sink To** = 目标 fixture / 新 spec 位置。
> 「≈」表示实现 agent 按断言等价性最终核数，可能有 ±1（如某「一条成功路径」归 keep 还是 sink 的边界）。

### 5.1 `IntegrationSessions`（17 文件 / 152 测试，共享 `MohistIntegrationFixture`）

| 文件 | 总 | Keep（契约） | Sink（计算） | Sink To |
|---|---|---|---|---|
| `Sessions/AgentSessionRecoveryApiSpecs.cs` | 16 | 5（404、inactive 200、conflict 200、shape、pi-bound 200） | **11**（idempotency key 重放、operation result reuse、reset new-key join pending、cancelled/timed-out 重试、recovery ambiguous runner result） | 新 `Specs/Sessions/AgentSessionRecoveryOrchestratorSpecs.cs`（`AgentSessionGrain`） |
| `Sessions/AgentSessionRecoveryConflictApiSpecs.cs` | 14 | 4（404×2、conflict 409、200） | **10**（idempotency key merge、omitted key 不重放、explicit-legacy key 不合并、recovery operation persistence） | 并入 `AgentSessionRecoveryOrchestratorSpecs` |
| `Sessions/GenericAgentSessionFollowupApiSpecs.cs` | 18 | 7（400×3、404、active session 200、runner offline 202、empty 200） | **11**（idle 启动 user turn、turn claim release、runner offline 排队、target resolution） | 新 `Specs/Sessions/GenericAgentSessionFollowupServiceSpecs.cs`（service → `MohistDb`）+ 新 `Specs/Sessions/GenericAgentSessionFollowupGrainSpecs.cs`（编排 → `AgentSessionGrain`） |
| `Sessions/GenericAgentSessionCancelApiSpecs.cs` | 17 | 4（already-ended 200、404×2、200） | **13**（cancel queued/executing/terminal 行为、stop 编排、turn claim release、reply-loss 重试） | 新 `Specs/Sessions/GenericAgentSessionStopServiceSpecs.cs`（`MohistDb`）+ `Specs/Sessions/GenericAgentSessionStopGrainSpecs.cs`（`AgentSessionGrain`） |
| `Sessions/GenericAgentSessionCanonicalFollowupApiSpecs.cs` | 10 | 4（404×2、200、binding 形状） | **6**（canonical alias 路由解析、target 优先级、runner offline 排队） | 并入 `GenericAgentSessionFollowupServiceSpecs` |
| `Sessions/AgentSessionScheduleApiSpecs.cs` | 13 | 6（400×3、404×2、schedule create 201） | **7**（same-key-same-body 重放、same-key-diff-body 409、key 省略跨请求不 dedup、list order、cancel 重放幂等） | 新 `Specs/Sessions/AgentSessionScheduleStoreSpecs.cs`（**先定位**：service → `MohistDb`；grain → `AgentSessionGrain`） |
| `Sessions/AgentSessionContextAssociationApiSpecs.cs` | 7 | 3（404×2、empty 200） | **4**（issue→sessions 关联、epic→sessions 关联、by epic number 解析） | 新 `Specs/Sessions/IssueSessionAssociationQuerierSpecs.cs`（**先定位**：service → `MohistDb`；grain → `AgentSessionGrain`） |
| `Sessions/AgentSessionReadApiSpecs.cs` | 11 | 4（404×2、JSON shape、transcript 过滤） | **7**（recency ordering、status filter、issue 关联投影、distinct list） | 新 `Specs/Sessions/AgentSessionListQuerierSpecs.cs`（`MohistDb`） |
| `Sessions/IssueWorkflowSessionHistorySpecs.cs` | 7 | 3（404 unknown issue、404 no history、project boundary 400） | **4**（active run 优先不 fallback、select newest matching、historical transcript filter by runtime id、commands 不 resolve historical） | 新 `Specs/Sessions/IssueWorkflowSessionHistoryQuerierSpecs.cs`（`MohistDb`） |
| `Sessions/AgentPathAmplificationSpecs.cs` | 10 | 6（404×2、400×2、alias 200、route→handler 200） | **4**（activity 候选裁剪、preview-only transcript 过滤、card 限 200、status 计数 truthfulness） | 新 `Specs/Sessions/AgentPathAmplificationQuerierSpecs.cs`（`MohistDb`） |
| `Sessions/AgentSessionActivityVisibilitySpecs.cs` | 6 | 3（generic 200、without-issue-ref 200、workflow 200） | **3**（active agent 列表、stale 排除、workflow card 不漏 agent 字段） | 新 `Specs/Sessions/AgentActivityCardQuerierSpecs.cs`（`MohistDb`） |
| `Sessions/GenericAgentSessionTranscriptAxisSpecs.cs` | 6 | 6（transcript 投影 + path 解析，路由+形状 契约为主；下沉收益小且易丢 DTO 映射） | 0 | — |
| `Sessions/SessionFollowupApiSpecs.cs` | 9 | 9（路由 scoping 契约 + 编排细节已被 `AgentSessionGrainFixture` 覆盖） | 0 | — |
| `Sessions/SessionTreeStopApiSpecs.cs` | 2 | 2（404×2 / 200 success path） | 0 | — |
| `Sessions/SessionTreeStopRetrySpecs.cs` | 2 | 2（stop retry 编排契约） | 0 | — |
| `Sessions/SessionTreeStopTargetAdapterSpecs.cs` | 1 | 1（target adapter 路由契约） | 0 | — |
| `Sessions/SessionTreeDetachApiSpecs.cs` | 3 | 3（detach 路由契约） | 0 | — |
| **小计** | **152** | **~72** | **~80** | — |

`IntegrationSessions` 整体下沉空间中等（~53%）；API 层的 152 测试主要在断言**路由形状 + recovery
idempotency contract + followup dispatch 形状**——前者在 HTTP 验更便宜（已 keep），后者下沉到
grain fixture 收益最大（recompute 编排省 web host 启动）。

> 注：3 个已不在 `IntegrationSessions` 集合的 session spec（`GenericAgentSessionSummarySpecs`、
> `UnifiedSessionRoutesSpecs`、`UnifiedSessionSummarySpecs`，共 51 测试）已迁到
> `MohistDbFixture` / `UnifiedSessionSummaryFactory` 轻 fixture，**不在本 plan 范围**。

### 5.2 `IntegrationIssue`（9 文件 / 119 测试，共享 `MohistIntegrationFixture`）

| 文件 | 总 | Keep（契约） | Sink（计算） | Sink To |
|---|---|---|---|---|
| `Issue/Api/IssueApiSpecs.cs` | 15 | 6（404 legacy route、404 project route、risk 200、create-with-profileId 200、list scoped 200、success path） | **9**（prereq→blocker 投影、create 序号、epic link primary、comments round-trip、system info 投影、status stage 派生） | 新 `Specs/Issue/Api/IssueApiContractSpecs.cs`（仅契约）+ `Specs/Issue/Querier/IssueQuerierProjectionSpecs.cs`（`MohistDb`）+ `Specs/Issue/Domain/IssueRiskPersistingSpecs.cs`（domain unit） |
| `Issue/Api/IssueMetricsApiSpecs.cs` | 20 | 5（404×3、400 unsupported bucket、400 unknown range） | **15**（30d/12w/90d/7d window、primary+previous 双窗、bucket 缩放、bucket 派生计数、stage duration 计算、approval wait 计算、quality primary+previous+trend） | 新 `Specs/Issue/Querier/IssueMetricsQuerierSpecs.cs`（`MohistDb` + `FakeTimeProvider`，**先定位** querier 边界） |
| `Issue/Api/IssueFeedbackApiSpecs.cs` | 17 | 4（409 non-awaiting、400 missing fields、404、JSON shape） | **13**（list ordering、stage filter、without feedback 空数组、stage state feedback scoping、open vs resolved 区分） | 新 `Specs/Issue/Querier/IssueFeedbackQuerierSpecs.cs`（`MohistDb`） |
| `Issue/Api/IssueRepositoryApiSpecs.cs` | 7 | 3（400 unknown repo、400 create 失败、404） | **4**（repo change→get 返回新 resolved、metadata change→返回 current、repo removed→返回 problem） | 新 `Specs/Issue/Services/IssueRepositoryResolverSpecs.cs`（`MohistDb`） |
| `Issue/Api/IssueRepositoryBindingApiSpecs.cs` | 14 | 5（400×2、400 不泄漏、404、200） | **9**（canonical casing、patch reassign、workflow start 后 reject、list filter 命中、historical target 保留、detail 投影） | 并入 `IssueRepositoryResolverSpecs` + 新 `Specs/Issue/Domain/IssueRepositoryBindingDomainSpecs.cs`（domain） |
| `Issue/Api/IssueArchivedDetailApiSpecs.cs` | 11 | 5（JSON shape×3、health-and-status 200、404） | **6**（workflow artifacts 保留、workflow events 合并 timeline、workflow status completed snapshot、execution history 字段一致、active alias 不暴露） | 新 `Specs/Issue/Querier/IssueArchivedDetailProjectionSpecs.cs`（`MohistDb`） |
| `Issue/Api/IssueWatchApiSpecs.cs` | 14 | 5（404×3、archived agent 404、404） | **9**（watching 状态机、muted 状态机、idempotent post/delete、detail 分组、list 投影、empty 数组） | 新 `Specs/Issue/Services/IssueWatchServiceSpecs.cs`（**先定位**：service → `MohistDb`；grain → `WorkflowGrain`） |
| `Issue/Api/IssueLabelsApiSpecs.cs` | 9 | 4（400×2、200 list、400×1） | **5**（list 投影 distinct、filter 命中、multi-filter 命中、full replace、create 持久化） | 新 `Specs/Issue/Querier/IssueLabelFilterQuerierSpecs.cs`（`MohistDb`） |
| `Issue/Api/IssuePatchRawPresenceMergeSpecs.cs` | 12 | 4（400、200×2、404） | **8**（raw presence merge 规则、null/omit 区分、merge 冲突、patch 副作用） | 新 `Specs/Issue/Domain/IssuePatchRawMergeDomainSpecs.cs`（domain unit） |
| **小计** | **119** | **~41** | **~78** | — |

`IntegrationIssue` 整体下沉空间大（~66%）：多数是 querier 投影 + 状态机 + repository 解析；
只有少量是路由+绑定+状态码契约。

### 5.3 `IssueLifecycle`（9 文件 / 64 测试，共享 `MohistIntegrationFixture`）

| 文件 | 总 | Keep（契约） | Sink（计算） | Sink To |
|---|---|---|---|---|
| `Issue/Api/IssueWorkflowLifecycleSpecs.cs` | 14 | 4（404 not active×2、400、redeliver dispatcher 形状） | **10**（start work 持久化、cancel workflow running 拒绝、cancel stopped→cancelled、workflow started 后 selection 改、unknown legacy 重新跑、stopped 启动新、active run 复用、prereq incomplete 不创建、dispatch vars 暴露、local path 字段无） | 新 `Specs/Issue/Grain/IssueWorkflowLifecycleGrainSpecs.cs`（`WorkflowGrain`） |
| `Issue/Api/IssueCompositeAdvancementApiSpecs.cs` | 10 | 3（400 父无 children、400、200） | **7**（parent→InProgress 聚合、all-children-blocked 仍 InProgress、detaching-last-child revert、redelivery idempotency、close parent 拒 non-terminal、reopen→backlog、archive cascade） | 新 `Specs/Issue/Grain/IssueCompositeAdvancementGrainSpecs.cs`（`WorkflowGrain`） |
| `Issue/Api/IssueStartReadinessApiSpecs.cs` | 12 | 5（draft default、ready 200、404、400 circular prereq、404） | **7**（omits eligibility ready、list 投影含 isDraft+canStart、draft blocker、waiting-for blocker、ready unblocked→pipeline、update→ready+blocker、waiting 仍 backlog、parent→composite 触发） | 新 `Specs/Issue/Domain/IssueStartReadinessDomainSpecs.cs`（domain）+ `Specs/Issue/Querier/IssueStartReadinessProjectionSpecs.cs`（`MohistDb`） |
| `Issue/Api/IssueSessionApiSpecs.cs` | 4 | 1（404、200、200、200 metadata JSON shape） | **3**（transcript segments 跨 batch 升序、不返 server-projected turns、context exhaustion category） | 新 `Specs/Issue/Querier/IssueSessionProjectionSpecs.cs`（`MohistDb`） |
| `Issue/Api/IssueManualDoneApiSpecs.cs` | 2 | 0 | **2**（stopped leaf→done、parent→reject） | 新 `Specs/Issue/Domain/IssueManualDoneDomainSpecs.cs`（domain） + `Specs/Issue/Grain/IssueManualDoneGrainSpecs.cs`（`WorkflowGrain`） |
| `Issue/Api/IssueCompositeChildProjectionApiSpecs.cs` | 1 | 0 | **1**（list+detail 暴露 additive children+blockedCount） | 并入 `IssueCompositeAdvancementGrainSpecs`（child 投影是同 grain） |
| `Issue/Grain/IssueCommentGrainSpecs.cs` | 2 | 0 | **2**（trim 持久化、invalid author 拒） | 已是 grain spec 但用 `MohistIntegrationFixture` → 迁到 `WorkflowGrainFixture`（同 fixture，零成本改造） |
| `Issue/Grain/IssueCompositeAdvancementGrainSpecs.cs` | 15 | 0 | **15**（fan-out 并发、parent NeverOwns、recompute 幂等、idempotent across redeliveries、mixed terminal aggregated、backlog no fan-out、zero children no-op、per-issue start path、parent→aggregate+fanout、restart after activation loss recovers、failure isolation） | 已是 grain spec 但用 `MohistIntegrationFixture` → 迁到 `WorkflowGrainFixture`（**集合归属：改 `IssueLifecycle` 为 `WorkflowGrain` 或保留 collection 仅换 fixture 注入**） |
| `Issue/Grain/IssueCompositeLifecycleGrainSpecs.cs` | 4 | 0 | **4**（close non-terminal child reject、reopen parent 不动 cancelled child、archive parent→terminal child、direct archive reject cancelled） | 迁到 `WorkflowGrainFixture` |
| **小计** | **64** | **~14** | **~50** | — |

`IssueLifecycle` 下沉空间**最大**（~78%）：绝大多数是 grain 编排（composite 状态机、workflow
lifecycle、readiness 投影），而 `MohistIntegrationFixture` 是为 HTTP 设计的——这里大部分
spec 用 `_fixture.Grains` 直调 grain（不走 HTTP），迁到 `WorkflowGrainFixture` 即可省 silo
外 + WebApplicationFactory。**这一族是 ROI 最高的子集**。

> 注：`IssueCompositeAdvancementGrainSpecs` 等 3 个 grain spec 已在 `IssueLifecycle` 集合但
> **用 `MohistIntegrationFixture`**（继承自早期 issue-419 T-002 模板），并未真正走 HTTP。迁到
> `WorkflowGrainFixture` 主要是 fixture 替换，**测试逻辑零改**；本 plan 在 A 批 #11-13 安排
> 这个「fixture 化」迁移。

## 6. 下沉汇总与 CI 节省预估

| 集合 | 总测试 | Keep | Sink | Sink 占比 |
|---|---|---|---|---|
| IntegrationSessions | 152 | ~72 | ~80 | 53% |
| IntegrationIssue | 119 | ~41 | ~78 | 66% |
| IssueLifecycle | 64 | ~14 | ~50 | 78% |
| **合计** | **335** | **~127** | **~208** | **62%** |

**CI 关键路径节省预估**（粗估，实现后以 `npm run test:budget` 实测为准）：
- `MohistIntegrationFixture` per-test 成本主要是 HTTP 往返 + DB 查询 + 断言（~50–150ms）。
  下沉到 `MohistDbFixture`（无 host 无 HTTP）后 per-test 降到 ~5–20ms；纯 domain unit <5ms；
  下沉到 `WorkflowGrainFixture`/`AgentSessionGrainFixture`（InProcessTestCluster，无端口）
  后 per-test 降到 ~20–60ms。
- 约 208 测试 × 节省 ~50–100ms ≈ **关键路径 ~10–21s**（集合内串行链直接缩短；并行集合的内存
  压力也下降——更少 silo+host 同时驻留）。
- 最大单批收益：`IntegrationIssue` querier 投影（~78 测试下沉到 `MohistDb`）+
  `IntegrationSessions` recovery / followup / cancel / schedule（~80 测试下沉到
  `AgentSessionGrainFixture`，省 web host ~80–100ms/条）+
  `IssueLifecycle`（~50 测试下沉到 grain fixture，省 HTTP 往返 ~80–100ms/条）。
- **这是估算**：p95/绝对上限仍由 `test-duration.config.jsonc` 守卫兜底；实现 PR 必须附
  `npm run test:budget` 前后对比作为证据。

> 注：下沉不减少**集合数**（fixture 仍需为契约测试保留），也不减少总测试条数（计算搬到下层仍
> 是等量测试），收益是**每条测试的执行成本**（去掉 HTTP/silo 往返）与**集合内串行链长度**。

## 7. 可执行迁移清单（按 ROI/风险分批）

> 每批 = 一个独立 PR（自洽、可回退）。每批完成定义：build 过 + 相关 track 测试过 +
> `npm run test:budget` 不退化 + spec-file-size baseline 不越桶（越桶同 commit 改）。命名按
> `design/testing.md` `*Specs.cs` / 上下文目录；新 spec 文件 < 24,000 字节（C# ratchet，
> 超过必须同 commit 改 `spec-file-size-baseline.json`）。

### 批次 A — 高 ROI 低风险（纯 querier/store/domain，目标 `MohistDb` / 纯 unit）【先做】

1. **`IssueMetricsQuerierSpecs`** ← `IssueMetricsApiSpecs` 的 15 计算（多窗口/桶粒度/stage duration
   /approval wait/quality primary+previous+trend）。`MohistDbFixture` + `FakeTimeProvider` 注入
   querier；**先定位** querier 边界。
2. **`IssueFeedbackQuerierSpecs`** ← `IssueFeedbackApiSpecs` 的 13 计算（list ordering / stage filter
   /stage state scoping / open vs resolved）。`MohistDbFixture`。
3. **`IssueRepositoryResolverSpecs` + `IssueRepositoryBindingDomainSpecs`** ←
   `IssueRepositoryApiSpecs`(4) + `IssueRepositoryBindingApiSpecs`(9) 的非契约部分。
   `MohistDbFixture`（resolver）+ 纯 domain unit。
4. **`IssueArchivedDetailProjectionSpecs`** ← `IssueArchivedDetailApiSpecs` 的 6 计算。
   `MohistDbFixture`。
5. **`IssueLabelFilterQuerierSpecs`** ← `IssueLabelsApiSpecs` 的 5 计算。`MohistDbFixture`。
6. **`IssueWatchServiceSpecs`**（**先定位**）← `IssueWatchApiSpecs` 的 9 计算。
7. **`IssuePatchRawMergeDomainSpecs`**（纯 unit）← `IssuePatchRawPresenceMergeSpecs` 的 8 计算。
8. **`IssueRiskPersistingDomainSpecs`**（纯 unit）← `IssueApiSpecs` 中的 risk 字段持久化。
9. **`IssueManualDoneDomainSpecs` + `IssueManualDoneGrainSpecs`**（grain 编排部分）←
   `IssueManualDoneApiSpecs` 的 2 计算。
10. **`IssueStartReadinessDomainSpecs` + `IssueStartReadinessProjectionSpecs`** ←
    `IssueStartReadinessApiSpecs` 的 7 计算。

### 批次 B — 中 ROI 中风险（querier + 部分状态机，目标 `MohistDb`）【次做】

11. **`AgentSessionSummaryAssemblerSpecs`** ← `GenericAgentSessionSummarySpecs` 的 14 计算。
    `MohistDbFixture`。
12. **`UnifiedSessionListQuerierSpecs` + `AgentSessionReadApiSpecs` 合并** ←
    `UnifiedSessionRoutesSpecs`(11) + `AgentSessionReadApiSpecs`(7) + `UnifiedSessionSummarySpecs`(12)。
    `MohistDbFixture`。
13. **`IssueSessionAssociationQuerierSpecs`**（**先定位**）← `AgentSessionContextAssociationApiSpecs` 的
    4 计算。
14. **`IssueWorkflowSessionHistoryQuerierSpecs`** ← `IssueWorkflowSessionHistorySpecs` 的 4 计算。
    `MohistDbFixture`。
15. **`AgentPathAmplificationQuerierSpecs` + `AgentActivityCardQuerierSpecs`** ←
    `AgentPathAmplificationSpecs`(4) + `AgentSessionActivityVisibilitySpecs`(3)。
    `MohistDbFixture`。

### 批次 C — 高 ROI 中风险（grain 编排，目标 `WorkflowGrain`）【独立 PR】

16. **`IssueCompositeAdvancementGrainSpecs`**（**迁 fixture**）←
    `IssueCompositeAdvancementGrainSpecs`(15) + `IssueCompositeAdvancementApiSpecs` 的 7 计算
    + `IssueCompositeChildProjectionApiSpecs`(1) + `IssueCompositeLifecycleGrainSpecs`(4)。
    用 `WorkflowGrainFixture`（`InProcessTestCluster`，无 web host）。**最大单批收益**。
17. **`IssueWorkflowLifecycleGrainSpecs`** ← `IssueWorkflowLifecycleSpecs` 的 10 计算。
    `WorkflowGrainFixture`。
18. **`IssueCommentGrainSpecs`**（**迁 fixture**）← `IssueCommentGrainSpecs` 的 2 计算。直接 fixture
    替换（用 `WorkflowGrainFixture`），零逻辑改动。
19. **`IssueSessionProjectionSpecs`** ← `IssueSessionApiSpecs` 的 3 计算。`MohistDbFixture`。
20. **`IssueQuerierProjectionSpecs`** ← `IssueApiSpecs` 的非契约部分（~5 计算）。`MohistDbFixture`。

### 批次 D — 待定位 + 高复杂度（grain 编排 / 决策点待定位）【后做】

21. **`AgentSessionRecoveryOrchestratorSpecs`**（**先定位**）← `AgentSessionRecoveryApiSpecs` 的
    11 计算 + `AgentSessionRecoveryConflictApiSpecs` 的 10 计算。grain → `AgentSessionGrain`。
22. **`GenericAgentSessionFollowupServiceSpecs` + `GenericAgentSessionFollowupGrainSpecs`** ←
    `GenericAgentSessionFollowupApiSpecs`(11) + `GenericAgentSessionCanonicalFollowupApiSpecs`(6)。
    service → `MohistDb`；grain → `AgentSessionGrain`。
23. **`GenericAgentSessionStopServiceSpecs` + `GenericAgentSessionStopGrainSpecs`** ←
    `GenericAgentSessionCancelApiSpecs` 的 13 计算。
24. **`AgentSessionScheduleStoreSpecs`**（**先定位**）← `AgentSessionScheduleApiSpecs` 的 7 计算。

### 不下沉（确认 keep 全部）

- `Sessions/GenericAgentSessionTranscriptAxisSpecs`(6)：transcript 投影 + path 解析，路由+形状
  契约为主，下沉收益小且易丢 DTO 映射。
- `Sessions/SessionTreeStopApiSpecs`(2)、`SessionTreeStopRetrySpecs`(2)、
  `SessionTreeStopTargetAdapterSpecs`(1)、`SessionTreeDetachApiSpecs`(3)、
  `SessionFollowupApiSpecs`(9)：路由 scoping + idempotency contract + 编排细节已被
  `AgentSessionGrainFixture` 内的 grain spec 覆盖，API 层只留路由+形状。
- `IntegrationSessions` 的 `AgentSessionReadApiSpecs` 中保持 4 测试 keep（list/distinct JSON
  形状、404×3）。
- `Issue/Api/IssueCompositeChildProjectionApiSpecs`(1)：已并入批次 C 的
  `IssueCompositeAdvancementGrainSpecs`。
- 任何「先定位」批次里的 keep-only 边界由实现 agent 现场定（按 §2 判据）。

## 8. 风险与缺口

1. **决策点待定位（批次 D / 批次 A 中带「先定位」项）**：querier vs grain 的归属需要在
   实现时 grep 确认；服务/编排边界清晰的可直接下沉，lock 在 grain 且无 service 缝的退化为
   grain-fixture sink（仍省 web host）。**这 4 批放最后，留缓冲**。
2. **`MohistIntegrationFixture` 不会消失**：host 路由（404、404 spoof-host、auth challenge、
   ProjectId 解析、admin route）必须在真 host 验证。下沉只缩短其串行链，不删除 fixture/集合。
3. **`WorkflowGrainFixture` 已含 IssueGrain + WorkflowGrain**（`Specs/Workflow/WorkflowGrainFixture.cs`）
   注册；`IssueCompositeAdvancementGrainSpecs` 跨 grain 编排可直接复用，无需新 fixture。
4. **`AgentSessionGrainFixture` 不含 IssueGrain**：跨 issue+session 编排的 spec 需评估是否能
   在该 fixture 内 + EF seed（用 `MohistDbFixture` + 模拟 session 状态）。
5. **DTO 映射覆盖**：下沉后 API 层只剩契约测试断言 `IssueDto` / `AgentSessionDto` 等 DTO
   形状；计算断言落在 querier / grain 的 `IssueProjection` / `AgentSessionSummary` 域对象。
   需确认 DTO 映射足够简单、由契约测试兜底；映射 bug 可能漏。原则：DTO 映射只在 route 一处，
   契约测试覆盖其字段存在性即可。
6. **CI 节省是估算**：实际节省取决于 per-test HTTP 往返占比；实现 PR 必须跑
   `npm run test:budget` 前后对比。若某批下沉后实测节省 <2s，评估是否值得维护双份（建议：
   下沉的等价测试要在 API 层**删除**原计算 case，不是新增平行——避免矩阵在两层并存，违背
   「一次行为变更一个文件」）。
7. **测试隔离**：`MohistDbFixture` 的 stores 在 collection 内共享；下沉 spec 须保证每测试
   自建数据或清理（参照现有 `IssueMetricsCompletionSpecs` 等 `MohistDb` 集合 spec 的隔离惯例），
   不得依赖顺序。
8. **spec-file-size ratchet**：当前 plan 涉及的最大源文件是
   `Issue/Api/IssueMetricsApiSpecs.cs`（30,033 字节）和
   `Issue/Api/IssueFeedbackApiSpecs.cs`（27,551 字节）；批次 A 拆分后单文件应明显下降。新
   spec 若 >24,000 字节需同 commit 改 `spec-file-size-baseline.json`（按 `design/testing.md`
   ratchet 规则）。
9. **与 #289 兄弟 issue 的资源协调**：`MohistIntegrationFixture` / `MohistDbFixture` 是共享
   的；#289 已批 A/B/C/D 落地后，#290 的批次 A/B/C/D 落地时需确认 fixture 注册与 collection
   定义未被破坏。建议 #290 批次 A 排 #289 批次 D 之后（避免 fixture 集合冲突）。
10. **`IssueCompositeAdvancementGrainSpecs` 跨 collection 迁移**（批次 C #16）：当前它在
    `IssueLifecycle` 集合但用 `MohistIntegrationFixture`；迁到 `WorkflowGrainFixture` 后保留
    collection 名（不动 collection 定义文件），仅改 fixture 注入。xUnit collection 与 fixture
    是正交的——collection 控制串行/并行，fixture 控制资源。

## 9. 范围之外 / 顺序约束

- **不改 `design/testing.md`**：本 plan 只落实其既有约定，不新增规则。
- **不实现**：本文件是 plan；§7 每批由后续 agent 执行，每批独立 PR、独立验收。
- **顺序**：A（querier/domain，零提取）→ B（更多 querier）→ C（grain 编排 + fixture 化）→
  D（待定位 + 高复杂度，留缓冲）。`#289` 批次 D 之后 → `#290` 批次 A → ... → `#290` 批次 D。
- **验收硬标准**（每批）：build 绿 + 相关 track 绿 + `npm run test:budget` 不退化（p95/绝对
  上限/deadline）+ 原集合文件 LOC 下降、未新增平行矩阵 + spec-file-size baseline 不越桶 +
  PR 描述里附「before / after per-test cost」实测。

---

## 附录 A：判定示例（佐证分类一致性）

- **Keep（契约）** `CreateIssue_OnLegacyCollectionRoute_ReturnsNotFound`：路由 + 状态码，必须
  在 HTTP。✅
- **Sink（计算）** `ListIssues_FilterByKeyValueLabel_OnlyReturnsMatching`：label filter 命中
  规则，是 `IssueQuerier` 的查询语义，改 filter 规则不应需要改 HTTP 测试。✅
- **边界** `IssueDetail_IncludesFeedbackArray`：既验证 detail 形状（契约）又含 feedback 数组
  （投影）。归 keep（作为「一条成功路径 + 形状」），feedback 数组的**stage scoping / ordering**
  由 sink 的 13 个计算 case 覆盖；此条不重复断言投影细节。✅
- **fixture 化迁移** `IssueCompositeAdvancementGrainSpecs` 已经是 grain spec 但用
  `MohistIntegrationFixture` → 迁到 `WorkflowGrainFixture`：测试逻辑零改，fixture 替换即可
  节省 web host 启动。归 C 批 #16。✅

## 附录 B：与 #289 兄弟 plan 的边界

| 范围 | SINK-PLAN-289 | SINK-PLAN-290（本） |
|---|---|---|
| Api 集合族 | `IntegrationApi`（21 文件 / 188） | `IntegrationIssue`（9 / 119） + `IntegrationSessions` 部分 |
| 调度/日志 | `IntegrationTelemetry`（4 / 63）、`IntegrationMisc`（8 / 50） | — |
| Issue 子族 | `IssueProfile`（3 / 19） | `IssueLifecycle`（9 / 64） |
| Sessions 族 | — | `IntegrationSessions`（17 / 152） |
| 重叠 | 无文件级重叠；`MohistIntegrationFixture` / `MohistDbFixture` 共享 | 同上 |
| 顺序 | #289 全部先于 #290 | #290 批次 A 在 #289 批次 D 之后启动 |

## 附录 C：分批落地文件清单速查

| 批 | 目标 fixture | 新 spec 文件（粗） | 下沉测试数 | 预计节省/测试 |
|---|---|---|---|---|
| A | `MohistDb` / 纯 unit | `IssueMetricsQuerier` / `IssueFeedbackQuerier` / `IssueRepositoryResolver` / `IssueRepositoryBindingDomain` / `IssueArchivedDetailProjection` / `IssueLabelFilterQuerier` / `IssueWatchService` / `IssuePatchRawMergeDomain` / `IssueRiskPersistingDomain` / `IssueManualDoneDomain+Grain` / `IssueStartReadinessDomain+Projection` | ~83 | ~80–120ms |
| B | `MohistDb` | `AgentSessionListQuerier` / `IssueSessionAssociationQuerier` / `IssueWorkflowSessionHistoryQuerier` / `AgentPathAmplificationQuerier` / `AgentActivityCardQuerier` | ~22 | ~50–100ms |
| C | `WorkflowGrain` | `IssueCompositeAdvancementGrain` / `IssueWorkflowLifecycleGrain` / `IssueCommentGrain`（迁 fixture）/ `IssueSessionProjection` / `IssueQuerierProjection` / `IssueManualDoneGrain` | ~50 | ~80–100ms（fixture 化节省 web host） |
| D | `AgentSessionGrain` / `MohistDb`（定位后） | `AgentSessionRecoveryOrchestrator` / `GenericAgentSessionFollowupService+Grain` / `GenericAgentSessionStopService+Grain` / `AgentSessionScheduleStore` | ~58 | ~30–60ms |
