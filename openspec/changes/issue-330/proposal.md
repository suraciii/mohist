## Why

Session 是横向叶子域，硬约束是"自身不反向依赖任何业务上下文"（`design/domain-analysis.md:72`、`design/context-map.md:105`）。但它的读侧 6 个文件寄生在 `Workflow/Services/Sessions/` 下，导致领域核心层 `Sessions/Services/AgentSessionQuery.cs:6` 必须 `using Mohist.Server.Workflow.Services.Sessions` 才能拿到 session label schema 常量 `AgentSessionQueryMetadataKeys`——叶子域反向依赖了 Workflow 目录。这个常量是 Session 自身的领域知识，放错位置让物理目录与子域归属错位，违反不变量。现在修是因为它是纯物理搬迁、零行为风险，且为 issue #327（拆分 `AgentSessionQuerier` 内部职责）扫清目录前置依赖。

## What Changes

- 把 `Workflow/Services/Sessions/` 下全部 6 个文件迁入 `Sessions/` 目录，使 `Workflow/Services/Sessions/` 目录消失：
  - `AgentSessionQueryMetadataKeys.cs`（label 键名常量）、`GenericAgentSessionMetadata.cs`、`WorkflowAgentSessionMetadata.cs`、`SessionTranscriptBuilder.cs`、`AgentSessionQuerier.cs` → `Sessions/Services/`
  - `AgentSessionReadModels.cs`（对外 DTO）→ `Sessions/`
- 所有迁移文件的 namespace 从 `Mohist.Server.Workflow.Services.Sessions` 改为 `Mohist.Server.Sessions.*`（与目标目录对齐）。
- 更新全部引用旧 namespace 的消费方 `using`（约 13 个 src 文件 + 20 个 test 文件）。注意方向性：Workflow 目录下的消费方（`WorkflowGrain`、`WorkflowSessionHealthService`、`WorkflowActivityQuerier`）改为 `using Mohist.Server.Sessions.*` 是合规的正向依赖，予以保留。
- **纯物理搬迁**：不改任何业务逻辑、字段形状、API 契约、label 键名字符串值。无 **BREAKING** 变更（namespace 是内部实现细节，非公开 API 契约）。

## Capabilities

- `session-domain-independence`: Session 作为横向叶子域的结构契约——读侧代码（查询器、对外 DTO、metadata 键名常量、transcript 投影）物理归拢在 `Sessions/` 目录下，namespace 统一为 `Mohist.Server.Sessions.*`，且 `Sessions/` 目录内任何文件不反向 `using Mohist.Server.Workflow.*`。本变更将该依赖方向不变量从注释提醒升级为被 spec 约束的契约，并以"`Workflow/Services/Sessions/` 目录消失"作为可验证终止条件。

## Impact

- **Server 源码**（`packages/server/src/Mohist.Server/`）：
  - 迁移 6 个文件：`Workflow/Services/Sessions/*` → `Sessions/`（5 进 `Sessions/Services/`，`AgentSessionReadModels.cs` 进 `Sessions/`）。
  - 更新消费方 `using`：`Api/`（`AgentSessionLaunchRoutes`、`AgentSessionListRoutes`、`AgentSessionFollowupRoutes`、`AgentSessionCancelRoutes`、`AgentSessionContextAssociationRoutes`、`AgentRoutes`、`IssueRoutes.Sessions`、`WorkflowSessionRoutes`、`RunnerRoutes`）、`Workflow/`（`WorkflowGrain`、`WorkflowSessionHealthService`、`WorkflowActivityQuerier`）、`Infrastructure/Data/Db/MohistDbContext.cs`、`Sessions/Services/AgentSessionQuery.cs`（切断反向 using）。
- **Server 测试**（`packages/server/tests/`）：约 20 个 spec/support 文件更新 `using`（含 `MigratedServicesRegistrationSpecs.cs` 的 DI 注册检查、`AgentSessionTestData.cs`）。
- **无 API 契约变更、无 schema 迁移、无依赖增减**。
- **回归兜底**：`TreatWarningsAsErrors` 拦截遗漏的 namespace 引用；约 29 个 Session spec + DI 注册检查 spec 全绿。
- **风险**（medium，承自 issue）：波及面广（~33 文件改 `using`），但机械且零行为变更，主要风险是漏改引用点，由编译 + spec 兜底。
