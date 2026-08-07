# SINK-PLAN-289 — IntegrationApi / IntegrationTelemetry / IntegrationMisc / IssueProfile 族集成测试下沉方案

> **状态**：设计阶段产出（#289）。本文是 plan，不改动任何测试或产品代码。
> 实现由后续 agent 按 §7 迁移清单分批执行。
> **依据**：`design/testing.md`「The lowest useful layer owns the behavior matrix. API/integration
> specs assert route, binding, status code, JSON shape, parameter parsing, and one success path per
> endpoint; state and calculation permutations belong to the querier/grain/domain specs below.」
>
> 本 plan 不修改 `design/testing.md`；它只把该既定约定落实到 #289 的四个集合。

## 1. 目标与范围

把四个走全栈 HTTP（每集合一个 `MohistIntegrationFixture` / `OtlpRoutesHostFixture`：独立 silo +
WebApplicationFactory + EF + SQLite）的 spec 集合里的**状态/计算排列**下沉到无 web host 的
轻量 fixture（`MohistDbFixture` 生产 DI 图 + 内存 SQLite，无 silo 无 host；`WorkflowGrainFixture`
InProcessTestCluster 内存传输无端口），把 API 层只留**契约断言**（路由 / 绑定 / 状态码 / JSON 形状 /
参数解析 / 每端点一条成功路径）。

审计范围（共 36 文件 / 320 测试）：

| 集合 (xUnit Collection) | 共享 Fixture | 文件数 | 测试数 |
|---|---|---|---|
| `IntegrationApi` | `MohistIntegrationFixture`（silo + web host） | 21 | 188 |
| `IntegrationTelemetry` | `OtlpRoutesHostFixture`（**两** web host + silo + 端口） | 4 | 63 |
| `IntegrationMisc` | `MohistIntegrationFixture` | 8 | 50 |
| `IssueProfile` | `MohistIntegrationFixture` | 3 | 19 |

`MohistIntegrationFixture` 与 `OtlpRoutesHostFixture` 是 CI 关键路径主体（每测试 50–150ms+ 的
HTTP 往返 + DB 查询；CI 4-vCPU 比本地慢 ~35%）。

## 2. 分类判据（契约 vs 计算）

**留下 API 层（契约）**——断言以下之一即可，且每端点只留**一条**成功路径：
- 路由可达 / 已移除路由返回 404
- 请求绑定（multipart / JSON 字段缺失 / content-type）→ 400/415
- 参数解析与校验（`limit` 越界、`range` 枚举、`types` 非法、host 路由）→ 400/404
- 状态码语义（201 创建、204 删除幂等、409 冲突、413 过大、401 认证）
- 响应 JSON 形状（envelope 字段、列表 vs 对象、`data` 结构）
- 单条成功路径（端到端走通一次）

**下沉（状态/计算排列）**——以下逻辑不该在 HTTP 层重复，属于 querier/grain/domain：
- 排序 / 分页 / 默认 limit / 跨页 tie-break
- 项目隔离 / 范围过滤 / 多源合并投影
- 状态转移（mark-read、archive、clear-on-switch、cleanup、idempotency dedup）
- 计算派生量（cost rollup、cost-per-ship 除零、timeseries 桶粒度、cap 截断指示位）
- 内容投影（activity-safe 字段过滤、redaction、logfmt 解析降级、envelope 上下文优先级）

判据一句话：**一次行为变更只能动一个测试文件**；如果改某计算逻辑会同时改 HTTP 层和下层两个文件，
HTTP 层那份就是该下沉的重复矩阵。

## 3. Fixture 盘点与能力矩阵

### 3.1 现有可下沉 fixture

| Fixture | 集合 | 能提供 | 不能提供 | 适用下沉 |
|---|---|---|---|---|
| `MohistDbFixture` (`Support/`) | `MohistDb` | 生产 DI 图（`ConfigureMohistServices`）、内存 SQLite（`MigratedSqliteTemplate.CopyTo`）、`InMemoryOtelDb`、所有 `IScopedService`/`ISingletonService`（Scrutor 自动注册：assembler / store / querier / `TaskLogStore` / `TraceQuerier` / `OtlpIngestGate`） | **无 web host、无 Orleans silo**（`Grains` 抛 `NotSupportedException`） | querier / assembler / store / 解析服务的**纯逻辑**测试 |
| `WorkflowGrainFixture` (`Specs/Workflow/`) | `WorkflowGrain` 等 | `InProcessTestCluster`（内存传输，无端口）、`IGrainFactory`、内存 SQLite、`RecordingEventStore`、`FakeTimeProvider` | 无 web host | grain 编排 / 状态转移 / 持久化 |
| `AgentSessionGrainFixture` (`Specs/Sessions/`) | （ grain 内部） | `InProcessTestCluster` + `FakeAgentSessionStore`/`TranscriptStore` + `TestSqliteDatabase` | 无 web host | AgentSession grain 行为（attachment acceptance 若落在 grain 侧） |

`IScopedService` 约定（`Infrastructure/Hosting/ServiceCollectionExtensions.cs`）经 Scrutor
`AddClasses(AssignableTo<IScopedService>)` 自动注册为自身 —— 这意味着
`ProjectEventFeedAssembler`、`ActivityEvidenceAssembler`、`TaskLogStore`、
`AgentJobArtifactUploadService` 等在 `MohistDbFixture.Services` 里都可直接 `GetRequiredService`，
**无需** web host。

### 3.2 Fixture 缺口（标注为风险，本 plan 不擅自补）

| 缺口 | 影响 | 处置建议 |
|---|---|---|
| **日志 tail 读取逻辑在 `LogsRoutes.ReadTailAsync` / `LogEntryProjection`（route 内私有 static），无独立 service** | `LogsRouteSpecs` 14 测试里的 11 个 cursor/截断/解析计算无法直接下沉到 service | 实现阶段**先抽取** `LogTailReader`（service）承载 cursor/linecap/logfmt 投影，再下沉；或接受「route-handler 直测」（仿 `ProjectEventTailApiSpecs.TailSession` 直调 handler 模式）。属**前置提取**，记为该批依赖。 |
| **attachment acceptance 决策位置待定**（可能在 `AgentSessionGrain` / input-attachment service 内） | `AgentSessionInputAttachmentAcceptanceSpecs` 9 测试需确认决策点 | 实现阶段先定位：若在 service → `MohistDbFixture`；若在 grain → `AgentSessionGrainFixture`（仍省 web host）。**该批最后做**。 |
| **`StartWorkAsync` 编排跨 IssueGrain→Workflow**（resolution 逻辑在 `IssueRepositoryResolver` service，编排在 grain） | `IssueWorkflowRepositoryResolutionSpecs` 是混合：resolution 决策可下沉 service，编排副作用需 grain | 拆两半：resolution 决策 → `MohistDbFixture` + `IssueRepositoryResolver`；no-workflow-on-failure / in-flight retention 编排 → `WorkflowGrainFixture`。 |

## 4. 服务边界图（端点 → 归属服务 → 下沉目标）

| 端点 | 归属服务（已确认） | 下沉 fixture |
|---|---|---|
| `GET /api/projects/{ref}/events` | `ProjectEventFeedAssembler`（`AgentOps/Services/`，`IScopedService`） | `MohistDbFixture` |
| `GET /api/projects/{ref}/events/tail` | `ProjectEventTailRoutes.HandleTailAsync` + `IEventTailSource`（已有 `InMemoryEventTailSource`） | handler 直测（无 host，fake project id） |
| `GET /api/projects/{ref}/activity`（evidence/waiting） | `ActivityEvidenceAssembler`（`AgentOps/Services/`，`IScopedService`） | `MohistDbFixture` |
| `GET /api/projects/{ref}/agent/cost` | cost-rollup querier（`AgentOps`，`IScopedService`） | `MohistDbFixture` |
| `GET /api/projects/{ref}/agent/usage` | usage-timeseries querier（`AgentOps`，`IScopedService`） | `MohistDbFixture` |
| `GET/POST /api/projects/{ref}/inbox*` | inbox store/querier（EF 行 + service） | `MohistDbFixture` |
| `GET/PUT /api/projects/{ref}/inbox/subscriptions` | inbox subscription store | `MohistDbFixture` |
| `GET/POST /api/labels*`（label catalog CRUD） | label catalog store/service | `MohistDbFixture` |
| `GET /api/projects/{ref}/issues/parent-candidates`、`/unread-count` | issue querier | `MohistDbFixture` |
| issue model↔variant 耦合规则 | issue domain（`Issue.Transitions`）/ service | `MohistDbFixture`（domain/store）或 issue domain unit |
| `POST/GET /api/.../tasks/.../logs` | `TaskLogStore`（`IScopedService`） | `MohistDbFixture` |
| `GET /api/templates` + variable extraction | `IssueTemplateRegistry`（`Issue/Services/IssueTemplates/`）+ 模板变量提取器 | `MohistDbFixture`（提取器可纯 unit） |
| `GET /api/logs` | `LogsRoutes.ReadTailAsync` + `ILogTailSource` | **需先抽 `LogTailReader`**（见 §3.2 缺口） |
| workflow artifact list/content | `IWorkflowArtifactStorage`（已有 `InMemoryWorkflowArtifactStorage`） | `MohistDbFixture` |
| `GET /api/otel/traces` + `POST /api/otel/query` | `TraceQuerier`（`ISingletonService`，亦 `IOtelQueryExecutor`；持 `ValidateSelectOnly`/cap/预算） | `MohistDbFixture`（seed `InMemoryOtelDb`，直调 `Execute`/`ValidateSelectOnly`） |
| OTLP ingest 写入/dedup/partial-success | `TraceIngester`（`Otel/`）+ `OtlpWriteBlockPlanner`/`IngestPreparation` | `MohistDbFixture`（直调 `TraceIngester`） |
| OTLP admission gate（4th-admit/429/lease） | `OtlpIngestGate`（`ISingletonService`） | `MohistDbFixture`（直调 gate） |
| dead-letter list redaction/filter | dead-letter querier/store | `MohistDbFixture` |
| issue workflow/workspace repository resolution | `IssueRepositoryResolver`（`Issue/Services/`） | resolution → `MohistDbFixture`；编排 → `WorkflowGrainFixture` |

## 5. 逐集合下沉分解

> 列说明：**Keep** = 留 API 层契约；**Sink** = 下沉计算；**Sink To** = 目标 fixture / 新 spec 位置。
> 「≈」表示实现 agent 按断言等价性最终核数，可能有 ±1（如某「一条成功路径」归 keep 还是 sink 的边界）。

### 5.1 `IntegrationApi`（21 文件 / 188 测试，共享 `MohistIntegrationFixture`）

| 文件 | 总 | Keep（契约） | Sink（计算） | Sink To |
|---|---|---|---|---|
| `Api/ProjectEventsApiSpecs.cs` | 25 | 3（404 unknown project、400 invalid `types`、+1 跨聚合成功路径形状） | **22**（默认/显式 limit、limit=0→200、cap、跨聚合降序、tie-break、亚秒排序、项目隔离×3、envelope 优先级×4、activity-safe 投影×3、attention/failure filter×2、大历史窗口、repo-change bucket、不新增事件） | 新 `Specs/AgentOps/ProjectEventFeedAssemblerSpecs.cs`（`MohistDb`） |
| `Api/ProjectEventTailApiSpecs.cs` | 11 | 2（400 invalid match、404 unknown project —— 唯二真 HTTP） | **9**（match 过滤、payload 不参与、项目隔离×2、NDJSON 形状×2、cancellation/release、no-replay） | 新 `Specs/Api/ProjectEventTailHandlerSpecs.cs`（直调 `HandleTailAsync` + fake project id + `InMemoryEventTailSource`，**无 host 无 DB**） |
| `Api/ActivityEvidenceApiSpecs.cs` | 5 | 2（400 limit 越界、+1 成功路径形状） | **3**（项目隔离/global runner、limit-after-merge 排序、默认 limit=100） | 新 `Specs/AgentOps/ActivityEvidenceAssemblerSpecs.cs`（`MohistDb`） |
| `Api/ActivityWaitingApiSpecs.cs` | 3 | 1（空 waiting 形状可作 keep 或随 sink） | **3**（approval-gate 检测、空集、仅 in-progress） | 并入上面 `ActivityEvidenceAssemblerSpecs`（waiting 分桶是同一 assembler） |
| `Api/AgentCostRollupApiSpecs.cs` | 11 | 3（404 unknown、400 unknown range、accepted ranges 200） | **8**（done 计数、cost-per-ship、real-zero、除零 undefined、90d 窗口缩放、all-time 不变、默认 30d） | 新 `Specs/AgentOps/AgentCostRollupQuerierSpecs.cs`（`MohistDb`） |
| `Api/AgentUsageTimeseriesApiSpecs.cs` | 5 | 3（404、400、accepted ranges） | **2**（七日桶结构、90d→weekly 桶粒度） | 新 `Specs/AgentOps/AgentUsageTimeseriesQuerierSpecs.cs`（`MohistDb`） |
| `Api/AttachmentApiSpecs.cs` | 8 | 3（size/count 400、stream 超 declared 400、+1 upload 成功路径） | **5**（bind/remove 生命周期、reject 不建 comment、项目隔离+持久化、create-issue bind pending、cleanup expired） | 新 `Specs/Api/AttachmentStorageSpecs.cs`（`MohistDb` + `InMemoryAttachmentStorage`） |
| `Api/InboxApiSpecs.cs` | 8 | 3（mark-read/archive/list 的 404×2 + 未知 project 404） | **5**（排序+排除 archived、mark-read 部分更新、mark-all-read、archive 移出默认列表、空项目） | 新 `Specs/Inbox/InboxQuerierSpecs.cs`（`MohistDb`） |
| `Api/InboxSubscriptionApiSpecs.cs` | 8 | 5（unknown key/missing key/non-object body/non-bool property 400×4 + unknown project 404） | **3**（默认五启用、put 持久化往返、项目隔离） | 新 `Specs/Inbox/InboxSubscriptionStoreSpecs.cs`（`MohistDb`） |
| `Api/IssueLowBandwidthApiSpecs.cs` | 6 | 4（parent/unread 的 404×2 + 静态资源 cache/brotli + html fallback 404） | **2**（parent 候选 eligibility 过滤、unread-count 项目内聚） | 新 `Specs/Issue/Querier/IssueLowBandwidthQuerierSpecs.cs`（`MohistDb`） |
| `Api/IssueModelVariantApiSpecs.cs` | 15 | 7（create/patch 往返×2、list 排除 detail 字段、invalid format 400×4、wrong-type metadata 400） | **8**（per-stage override×2、clear-model 清 variant、clear-stage 清 stage-variant、switch-model 丢弃 stale variant、empty-model 清 stale、catalog 选择+默认、absent-from-catalog 可配） | 新 `Specs/Issue/Domain/IssueModelVariantCouplingSpecs.cs`（domain/store unit；catalog 部分入 querier） |
| `Api/LogsRouteSpecs.cs` | 14 | 3（响应形状、400 invalid params、source=active 文件名 可作 keep） | **11**（line-cap 截断+cursor、shrink reset、无新行、auto-follow、max-bytes 行截断、non-json 降级、invalid structured 降级、混合 logfmt、logfmt round-trip、目录缺失/文件缺失 unavailable） | 新 `Specs/Api/LogTailReaderSpecs.cs`（**前置：抽 `LogTailReader` service** + `InMemoryLogTailSource`，`MohistDb` 或纯 unit） |
| `Api/TaskLogRouteSpecs.cs` | 11 | 6（malformed json 400、duplicate seq 409、invalid metadata/oversized 400、no-grain 副作用、unknown owner 404、empty-page 形状） | **5**（store+accepted count、agent-job owner kind 路由、分页 seq 序、unknown task 空页、issue 无 workflow 空页） | 新 `Specs/Agent/Storage/TaskLogStoreSpecs.cs`（`MohistDb` + `TaskLogStore`） |
| `Api/TemplateRoutesSpecs.cs` | 9 | 5（list 内建 sorted、proposal frontmatter+body 形状、empty/whitespace/missing body 400×3） | **4**（变量提取 valid/unresolvable/escaped/context-error） | 新 `Specs/Issue/IssueTemplate/TemplateVariableExtractorSpecs.cs`（**纯 unit**，无 fixture） |
| `Api/WorkflowArtifactQueryRouteSpecs.cs` | 11 | 3（content stream、reject 跨 issue 上下文、DTO naming） | **8**（latest-per-path、path history 序、无 history 只 latest、task-run filter、issue 无 workflow 空、目录 listing、目录 entry bytes、目录 appears-as-collection） | 新 `Specs/Workflow/Storage/WorkflowArtifactQuerySpecs.cs`（`MohistDb` + `InMemoryWorkflowArtifactStorage`） |
| `Api/WorkflowArtifactUploadRouteSpecs.cs` | 8 | 7（accept multipart、same-key-diff-hash 409、unknown work 404、non-multipart/missing-path/malformed-dir 400×3、agent-job 成功） | **1**（same-key-same-hash 幂等） | 并入上面 artifact storage spec（幂等是存储语义） |
| `Api/AgentSessionInputAttachmentAcceptanceSpecs.cs` | 9 | 0–1（launch 成功路径可留 1） | **8–9**（attachments-only accept、content 可用、empty→input-required、mixed accept/reject reason、already-bound reject、survive reload+dispatch descriptor、followup variants×3） | **待定位**：service→`MohistDb`；grain→`AgentSessionGrainFixture`。**该批最后做** |
| `Api/ApiContractSpecs.cs` | 13 | 10（404×2 theory、DNS name 验证、name/id 解析、project-name ref、global runner 成功路径、runner JSON 形状、rebase 成功+409、agent-status 成功路径） | **3**（model-variants map、no-variants omit、disjoint runner union —— 变体聚合矩阵） | 新 `Specs/Runner/.../RunnerModelVariantAggregatorSpecs.cs`（`MohistDb` 或 grain） |
| `Api/AgentSessionSpawnRouteSpecs.cs` | 3 | 2（workspace-mode retired 拒绝码、tree-page 形状） | **1**（prompt 空白 idempotency fingerprint） | 新 fingerprint 计算的 unit（若 fingerprint 有独立 service）；否则留 |
| `Api/RoutingTestRoutesSpecs.cs` | 2 | 2（空状态形状、trace 形状 —— 均为路由契约，trace 排序逻辑量小） | 0 | — |
| `Api/AgentSessionAttachmentApiSpecs.cs` | 3 | 3（owning 成功、跨 session 404、unbound 404 —— 全是 content 路由 scoping 契约） | 0 | — |
| **小计** | **188** | **~75** | **~113** | — |

### 5.2 `IntegrationTelemetry`（4 文件 / 63 测试，共享 `OtlpRoutesHostFixture` —— 最重 fixture）

| 文件 | 总 | Keep（契约） | Sink（计算） | Sink To |
|---|---|---|---|---|
| `Telemetry/OtelQueryRoutesIntegrationSpecs.cs` | 33 | 20（envelope 形状、empty、host-spoof 404、413 oversized body、multi-stmt/non-select/missing-field/null/invalid-json 400×N、sql-syntax/no-such-table 400 映射、attach 拒绝、body-at-limit 边界、status online/offline/no-inspect） | **13**（traces 降序/limit/service-filter/combine/max-cap×5、execution budget cancel、client cancel、row-cap/byte-cap/wide-col/moderate-rows/recursive-cte 截断指示位×6） | 新 `Specs/SystemSpecs/Otel/TraceQuerierSpecs.cs`（`MohistDb` + `InMemoryOtelDb`，直调 `Execute`/`ValidateSelectOnly`） |
| `Telemetry/OtlpRoutesBoundedIngressSpecs.cs` | 15 | 9（over-limit json/protobuf 413、malformed-after-admit 400、unsupported-media 415、wire contract json/protobuf×2、5th-protobuf-429） | **6**（4th-admit signal、5th-json-429 计数、reject-without-read、不 publish outcome、不持久化、lease-release/fourth-provisional/exception-lets-through） | 新 `Specs/SystemSpecs/Otel/OtlpIngestGateSpecs.cs`（`MohistDb`，直调 `OtlpIngestGate`） |
| `Telemetry/OtlpRoutesBoundedWriteSpecs.cs` | 4 | 0 | **4**（跨 block 全持久化、reject-all partial-success count、duplicate correction、serialized gate） | 新 `Specs/SystemSpecs/Otel/TraceIngesterSpecs.cs`（`MohistDb`，直调 `TraceIngester`） |
| `Telemetry/OtlpRoutesIntegrationSpecs.cs` | 11 | 7（missing-enablement 200、explicit-false 路由省略、415、invalid-json 400、empty 200、main-host 不 invoke×2） | **4**（persist json/protobuf×2、protobuf parse required fields、duplicate idempotent） | 并入 `TraceIngesterSpecs`（持久化/dedup 是 ingester 语义） |
| **小计** | **63** | **~36** | **~27** | — |

`OtlpRoutesHostFixture` 起**两个** web host + silo + 端口分配 —— 把 query/ingest/write/gate 计算
下沉后，该 fixture 只剩 host 路由 + binding + status code 契约，fixture 仍需保留（host 路由必须在
真 host 验证），但集合内串行链大幅缩短。

### 5.3 `IntegrationMisc`（8 文件 / 50 测试）

| 文件 | 总 | Keep（契约） | Sink（计算） | Sink To |
|---|---|---|---|---|
| `Auth/AuthResolutionSpecs.cs` | 13 | **13**（全部：401/challenge/cookie/bearer 优先级/query-token 拒绝/health/device/github-exempt/hub-negotiate —— 认证中间件行为属路由+状态码契约，必须在 HTTP 验证） | 0 | — |
| `Auth/OperatorCredentialMigrationSpecs.cs` | 4 | **4**（legacy header 在各路由被拒/被忽略 —— 弃用契约） | 0 | — |
| `Events/DispatcherStartupSpecs.cs` | 1 | **1**（host 启动激活 dispatcher + reminder —— 必须 host） | 0 | — |
| `Events/EventPushRegistrationSpecs.cs` | 2 | **2**（event-bridge / runner-terminal 不在 durable subscriptions —— 注册契约） | 0 | — |
| `Foundation/HttpApiJsonWiringSpecs.cs` | 4 | **4**（JSON serializer options 一致性 wiring —— 已是同步 `[Fact]`，无 HTTP，零成本） | 0 | — |
| `Events/DeadLetterRoutesSpecs.cs` | 7 | 4（redeliver event-bridge 拒绝、out-of-range limit 400、loopback 边界、unauth 拒绝+无副作用） | **3**（list handler filter、redact stack/path、redact UNC path） | 新 `Specs/Events/DeadLetterQuerierSpecs.cs`（`MohistDb`） |
| `Label/Api/LabelCatalogApiSpecs.cs` | 18 | 14（CRUD 201/409/400×N/204、empty、list、distinct-keys、各 400 不 mutate） | **4**（patch supported-only 保 description、clear supported-only、project scoping、post supported-values 持久化） | 新 `Specs/Label/LabelCatalogStoreSpecs.cs`（`MohistDb`） |
| `SystemSpecs/RuntimeSettingsSpecs.cs` | 1 | 0 | **1**（patch project variables → runtime preference 解析） | 新 `Specs/SystemSpecs/RuntimeSettingsServiceSpecs.cs`（`MohistDb`） |
| **小计** | **50** | **~42** | **~8** | — |

`IntegrationMisc` 大头是 Auth（17 测试）+ 零成本同步 wiring（4），均不可下沉；本集合下沉收益小（~8 测试）。

### 5.4 `IssueProfile`（3 文件 / 19 测试）

| 文件 | 总 | Keep（契约） | Sink（计算） | Sink To |
|---|---|---|---|---|
| `Issue/Api/IssueWorkflowRepositoryResolutionSpecs.cs` | 6 | 1（resolution-failure 不建 workflow —— 可作编排契约 keep，或随 sink） | **5**（resolve-from-config+dispatch vars、config-change 用最新、referenced-removed 抛 problem、default-repo upgrade、in-flight workflow 保留 vars） | 拆：resolution 决策 → 新 `Specs/Issue/Services/IssueRepositoryResolverSpecs.cs`（`MohistDb`）；编排副作用 → `Specs/Issue/Grain/...`（`WorkflowGrain`） |
| `Issue/Api/IssueWorkspaceRepositoryResolutionSpecs.cs` | 5 | 1（removed → API 返回 RepositoryConfigurationProblem 是错误响应契约） | **4**（config-change→baseBranch 取 run snapshot、removed→rebase 用 snapshot、baseBranch-change→rebase 用 snapshot、removed→archive 抛 problem） | 新 `Specs/Issue/Services/WorkspaceRepositorySnapshotSpecs.cs`（`MohistDb`；snapshot 偏好逻辑） |
| `Issue/IssueTemplate/IssueTemplateApiSpecs.cs` | 8 | 5（list 内建、get feature 全文、alias mohist-default、404、无 project-id 400） | **3**（disabled 排除内建、disabled 可被 project 自定义 shadow、disabled 不影响其他 project） | 新 `Specs/Issue/IssueTemplate/IssueTemplateRegistrySpecs.cs`（`MohistDb` + `IssueTemplateRegistry`） |
| **小计** | **19** | **~7** | **~12** | — |

## 6. 下沉汇总与 CI 节省预估

| 集合 | 总测试 | Keep | Sink | Sink 占比 |
|---|---|---|---|---|
| IntegrationApi | 188 | ~75 | ~113 | 60% |
| IntegrationTelemetry | 63 | ~36 | ~27 | 43% |
| IntegrationMisc | 50 | ~42 | ~8 | 16% |
| IssueProfile | 19 | ~7 | ~12 | 63% |
| **合计** | **320** | **~160** | **~160** | **50%** |

**CI 关键路径节省预估**（粗估，实现后以 `npm run test:budget` 实测为准）：
- 共享 fixture 集合内测试**不各自起 host**，per-test 成本主要是 HTTP 往返 + DB 查询 + 断言
  （~50–150ms）。下沉到 `MohistDbFixture`（无 host 无 HTTP）后 per-test 降到 ~5–20ms；纯 unit（template
  变量提取）<5ms。
- 约 160 测试 × 节省 ~100–130ms ≈ **关键路径 ~16–21s**（集合内串行链直接缩短；并行集合的内存压力
  也下降——更少 silo+host 同时驻留）。
- 最大单批收益：`IntegrationApi`（~113 测试下沉）+ `OtlpRoutesHostFixture`（两 host，~27 下沉）。
- **这是估算**：p95/绝对上限仍由 `test-duration.config.jsonc` 守卫兜底；实现 PR 必须附 `npm run test:budget`
  前后对比作为证据。

> 注：下沉不减少**集合数**（fixture 仍需为契约测试保留），也不减少总测试条数（计算搬到下层仍是
> 等量测试），收益是**每条测试的执行成本**（去掉 HTTP/silo 往返）与**集合内串行链长度**。

## 7. 可执行迁移清单（按 ROI/风险分批）

> 每批 = 一个独立 PR（自洽、可回退）。每批完成定义：build 过 + 相关 track 测试过 + `npm run test:budget`
> 不退化 + spec-file-size baseline 不越桶（越桶同 commit 改）。命名按 `design/testing.md`
> `*Specs.cs` / 上下文目录；新 spec 文件 <800 LOC（C# ratchet）。

### 批次 A — 高 ROI 低风险（纯 querier/assembler，目标 `MohistDb`）【先做】
1. **`ProjectEventFeedAssemblerSpecs`** ← `ProjectEventsApiSpecs` 的 22 个计算；API 层留 3 契约。
   - 下沉方式：`MohistDbFixture`，复用现有 `ProjectEventsApiTestSupport` 的 seed helpers（`SeedIssueEventHistoryAsync`/`AppendIssueEventAsync` 等已直写 DB），直接 `GetRequiredService<ProjectEventFeedAssembler>()` 调用，断言 `ProjectEventEnvelope[]`（同字段等价，不再走 DTO 但 DTO 映射在 route 仅 1 处、契约测试覆盖）。
2. **`AgentCostRollupQuerierSpecs`** ← `AgentCostRollupApiSpecs` 的 8 计算。
3. **`AgentUsageTimeseriesQuerierSpecs`** ← `AgentUsageTimeseriesApiSpecs` 的 2 计算。
4. **`ActivityEvidenceAssemblerSpecs`**（含 waiting 分桶）← `ActivityEvidenceApiSpecs`(3) + `ActivityWaitingApiSpecs`(3)。
5. **`InboxQuerierSpecs` / `InboxSubscriptionStoreSpecs`** ← `InboxApiSpecs`(5) + `InboxSubscriptionApiSpecs`(3)。
6. **`TaskLogStoreSpecs`** ← `TaskLogRouteSpecs` 的 5 计算（直调 `TaskLogStore`）。
7. **`WorkflowArtifactQuerySpecs`**（list/content/目录，含 upload 幂等 1）← artifact query(8) + upload(1)。
8. **`LabelCatalogStoreSpecs`** ← `LabelCatalogApiSpecs`(4)。
9. **`DeadLetterQuerierSpecs`** ← `DeadLetterRoutesSpecs`(3)。
10. **`IssueTemplateRegistrySpecs`** ← `IssueTemplateApiSpecs`(3)。

### 批次 B — 中 ROI 中风险（telemetry，目标 `MohistDb` + 直调 service）【次做】
11. **`TraceQuerierSpecs`** ← `OtelQueryRoutesIntegrationSpecs`(13)：seed `InMemoryOtelDb`，直调
    `TraceQuerier.Execute` / `ValidateSelectOnly`，覆盖 row/byte cap 截断指示位 + budget/cancel + traces
    排序/limit/filter。
12. **`TraceIngesterSpecs`** ← `OtlpRoutesBoundedWriteSpecs`(4) + `OtlpRoutesIntegrationSpecs`(4)：直调
    `TraceIngester`，覆盖跨 block 持久化 / partial-success count / dedup / serialized gate / protobuf parse。
13. **`OtlpIngestGateSpecs`** ← `OtlpRoutesBoundedIngressSpecs`(6)：直调 `OtlpIngestGate`，覆盖 4th-admit /
    5th-429 / lease-release / 不持久化。
    - 保留 `OtlpRoutesHostFixture` 仅承载 host 路由 + binding + status code 契约（413/415/400/200/spoof-404）。

### 批次 C — 需前置提取（中风险）【独立 PR】
14. **抽取 `LogTailReader` service** ← 把 `LogsRoutes.ReadTailAsync` + `LogEntryProjection` 抽成
    `IScopedService`（行为不变，route 改为调 service），再下沉 `LogsRouteSpecs`(11) 到 `LogTailReaderSpecs`
    + `InMemoryLogTailSource`。**提取与下沉同 PR**，附 route 行为不变的契约佐证（保留 3 个 keep 契约）。

### 批次 D — 混合下沉（grain 编排，目标 `WorkflowGrain` / `AgentSessionGrain`）【后做】
15. **`IssueRepositoryResolverSpecs`（`MohistDb`）+ 编排 spec（`WorkflowGrain`）** ←
    `IssueWorkflowRepositoryResolutionSpecs`(5) + `IssueWorkspaceRepositoryResolutionSpecs`(4)。
16. **`IssueModelVariantCouplingSpecs`** ← `IssueModelVariantApiSpecs`(8)：model↔variant 耦合规则落
    issue domain/store；catalog 选择部分落 querier。
17. **`RunnerModelVariantAggregatorSpecs`** ← `ApiContractSpecs`(3 变体聚合矩阵)。
18. **Attachment acceptance**（`AgentSessionInputAttachmentAcceptanceSpecs` 8–9）← **先定位决策点**
    （service vs grain），再选 fixture；该批最后做。
19. **`ProjectEventTailHandlerSpecs`** ← `ProjectEventTailApiSpecs`(9)：直调 `HandleTailAsync` + fake
    project id + `InMemoryEventTailSource`，无 host 无 DB。
20. **`TemplateVariableExtractorSpecs`（纯 unit）** ← `TemplateRoutesSpecs`(4)。
21. **`IssueLowBandwidthQuerierSpecs`** ← `IssueLowBandwidthApiSpecs`(2)。
22. **`AttachmentStorageSpecs`** ← `AttachmentApiSpecs`(5)。
23. **`RuntimeSettingsServiceSpecs`** ← `RuntimeSettingsSpecs`(1)；`AgentSessionSpawnRoute` fingerprint(1) 视定位。

### 不下沉（确认 keep 全部）
- `IntegrationMisc` 的 Auth(17) + OperatorCredentialMigration(4) + DispatcherStartup(1) +
  EventPushRegistration(2) + HttpApiJsonWiring(4)：认证/弃用/启动/注册/wiring 均为路由+状态码+形状契约，
  必须在 HTTP/host 层验证，零下沉价值。
- `RoutingTestRoutesSpecs`(2)、`AgentSessionAttachmentApiSpecs`(3)：纯路由 scoping/形状契约。

## 8. 风险与缺口

1. **前置提取（批次 C）是硬依赖**：`LogsRouteSpecs` 的 11 计算 lock 在 route 私有方法里，不抽
   `LogTailReader` 无法下沉。若不想提取，可退化为「route-handler 直测」（仿 TailSession），但那把
   route handler 当 SUT，不如抽 service 干净——建议提取。
2. **attachment acceptance / issue repo resolution 决策点待定位**（批次 D）：实现 agent 须先 grep 确认
   决策在 service 还是 grain，再定 fixture；若 lock 在 grain 且无 service 缝，可能需要小范围重构或
   退化为 grain-fixture sink（仍省 web host）。**这两批放最后，留缓冲。**
3. **DTO 映射覆盖**：下沉后 API 层只剩契约测试断言 `ProjectEventDto` 等 DTO 形状；计算断言落在
   assembler 的 `ProjectEventEnvelope`。需确认 DTO 映射（`ProjectEventDto.From`）足够简单、由契约测试
   兜底，否则映射 bug 会漏。原则：DTO 映射只在 route 一处，契约测试覆盖其字段存在性即可。
4. **`OtlpRoutesHostFixture` 不会消失**：host 路由（main vs otlp 端口、spoof host 404、enablement
   省略路由）必须在真 host 验证。下沉只缩短其串行链，不删除 fixture/集合。
5. **CI 节省是估算**：实际节省取决于 per-test HTTP 往返占比；实现 PR 必须跑 `npm run test:budget`
   前后对比。若某批下沉后实测节省 <2s，评估是否值得维护双份（建议：下沉的等价测试要在 API 层
   **删除**原计算 case，不是新增平行——避免矩阵在两层并存，违背「一次行为变更一个文件」）。
6. **测试隔离**：`MohistDbFixture` 的 stores 在 collection 内共享；下沉 spec 须保证每测试自建数据
   或清理（参照现有 `MohistDb` 集合 spec 的隔离惯例），不得依赖顺序。
7. **spec-file-size ratchet**：新 spec 若 >24,000 字节需同 commit 改 `spec-file-size-baseline.json`；
   大集合（ProjectEvents 22 case）注意按行为再拆文件。

## 9. 范围之外 / 顺序约束

- **不改 `design/testing.md`**：本 plan 只落实其既有约定，不新增规则。若下沉过程发现约定需补充
  （如 handler 直测模式是否算 spec track），单开 issue 讨论，不在此 plan 内改。
- **不实现**：本文件是 plan；§7 每批由后续 agent 执行，每批独立 PR、独立验收。
- **顺序**：A（纯 querier，零提取）→ B（telemetry 直调 service）→ C（logs 提取，硬依赖）→ D（grain/
  待定位，留缓冲）。Misc 的 keep 全部、IssueProfile 的混合下沉可与 D 并行。
- **验收硬标准**（每批）：build 绿 + 相关 track 绿 + `npm run test:budget` 不退化（p95/绝对上限/deadline）
  + 原集合文件 LOC 下降、未新增平行矩阵 + spec-file-size baseline 不越桶。

---

## 附录：判定示例（佐证分类一致性）

- **Keep（契约）** `GetProjectEvents_UnknownProject_Returns404`：路由 + 状态码，必须在 HTTP。✅
- **Sink（计算）** `GetProjectEvents_DefaultLimit_ReturnsMostRecentFirst`：默认 limit=200 + 降序，是
  `ProjectEventFeedAssembler` 的查询语义，改 limit 默认值不应需要改 HTTP 测试。✅
- **边界** `GetCost_ReturnsEnvelopeWithAllFourFields`：既验证 envelope 形状（契约）又含四字段（计算
  派生）。归 keep（作为「一条成功路径 + 形状」），四字段的**派生规则**（done-only 计数、除零等）由
  sink 的 8 个计算 case 覆盖；此条不重复断言派生细节。✅
