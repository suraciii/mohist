### Requirement: Session 读侧物理归拢于 Sessions 目录

Session 作为横向叶子域，其读侧代码（查询器、对外 DTO、metadata 键名常量、transcript 投影、generic/workflow session metadata 构造）SHALL 物理归拢在 `packages/server/src/Mohist.Server/Sessions/` 目录树下。寄生目录 `Workflow/Services/Sessions/` MUST 在本次变更后消失。

具体放置规则：
- `AgentSessionQueryMetadataKeys.cs`、`GenericAgentSessionMetadata.cs`、`WorkflowAgentSessionMetadata.cs`、`SessionTranscriptBuilder.cs`、`AgentSessionQuerier.cs` → `Sessions/Services/`
- `AgentSessionReadModels.cs` → `Sessions/`

迁移 MUST 是纯物理搬迁：不改任何业务逻辑、字段形状、API 契约、label 键名字符串值。

#### Scenario: 寄生目录消失
- **WHEN** 迁移完成后检查 `packages/server/src/Mohist.Server/Workflow/Services/Sessions/`
- **THEN** 该目录不再存在，其下原有的 6 个文件全部位于 `Sessions/` 目录树内

#### Scenario: 读侧文件就位
- **WHEN** 枚举 `Sessions/` 目录树
- **THEN** `AgentSessionQuerier`、`AgentSessionReadModels`、`AgentSessionQueryMetadataKeys`、`GenericAgentSessionMetadata`、`WorkflowAgentSessionMetadata`、`SessionTranscriptBuilder` 六个类型均出现在 `Sessions/` 之下（5 个在 `Sessions/Services/`，DTO 在 `Sessions/`）

### Requirement: 迁移文件 namespace 与目标目录对齐

每个迁移文件声明的 namespace SHALL 与其新的物理目录对齐，统一为 `Mohist.Server.Sessions.*`。任何迁移文件 MUST 不再声明 `Mohist.Server.Workflow.Services.Sessions` namespace。

#### Scenario: namespace 改写
- **WHEN** 检查每个迁移文件的 `namespace` 声明
- **THEN** 文件位于 `Sessions/` 下者声明 `Mohist.Server.Sessions`，位于 `Sessions/Services/` 下者声明 `Mohist.Server.Sessions.Services`，且无文件声明 `Mohist.Server.Workflow.Services.Sessions`

### Requirement: Sessions 目录切断对 Workflow 目录的反向依赖

Session 是横向叶子域，硬约束是自身不反向依赖任何业务上下文（`design/domain-analysis.md`、`design/context-map.md`）。`Sessions/` 目录树下任何文件 MUST 不含 `using Mohist.Server.Workflow.*` 形式的 using 引入语句。本次变更 MUST 消除现存于 `Sessions/Services/AgentSessionQuery.cs` 的 `using Mohist.Server.Workflow.Services.Sessions`（为获取 `AgentSessionQueryMetadataKeys` 而引入的反向依赖）。

注：该不变量针对 *using 引入语句*；`AgentSessionQuerier` 通过全限定名（如 `Workflow.Domain.Run.TaskRunStatus.Running`）引用 Workflow 类型不属于此约束范围。

#### Scenario: 反向 using 被切断
- **WHEN** 在 `Sessions/` 目录树下搜索 `using Mohist.Server.Workflow.` 形式的语句
- **THEN** 无任何匹配结果

#### Scenario: 标签键名常量本地可得
- **WHEN** `Sessions/Services/AgentSessionQuery.cs` 需引用 `AgentSessionQueryMetadataKeys`
- **THEN** 该常量已位于 `Sessions/Services/`（同 namespace `Mohist.Server.Sessions.Services`），`AgentSessionQuery.cs` 不再需要任何 `using Mohist.Server.Workflow.*` 即可访问

### Requirement: 保留 Workflow 对 Session 读模型的正向依赖

本变更只消除 Session→Workflow 的反向依赖，不删除 Workflow→Session 的合规正向依赖。Workflow 目录下的消费方（`WorkflowGrain`、`WorkflowSessionHealthService`、`WorkflowActivityQuerier` 等）消费 Session 读模型（DTO、查询器、metadata 构造）SHALL 继续可用，引用方 SHALL 改为 `using Mohist.Server.Sessions.*` 形式的正向 using。

#### Scenario: Workflow 消费方改写为正向 using
- **WHEN** 检查 `Workflow/` 目录树下的消费方文件
- **THEN** 其对原 `Mohist.Server.Workflow.Services.Sessions` 类型的引用改为 `using Mohist.Server.Sessions.*`，且引用关系保持可用（编译通过）

### Requirement: 全部消费方 using 同步更新

所有引用旧 namespace `Mohist.Server.Workflow.Services.Sessions` 的 src 与 test 文件（Api 路由、`MohistDbContext`、Workflow 消费方、测试 spec/support）SHALL 在本次变更中更新为对应的 `Mohist.Server.Sessions.*` using，且不残留任何对旧 namespace 的引用。

#### Scenario: 旧 namespace 引用清零
- **WHEN** 在 `packages/server/` 下搜索 `Mohist.Server.Workflow.Services.Sessions`
- **THEN** src 与 test 中无任何 `using Mohist.Server.Workflow.Services.Sessions` 引用，也无任何 `namespace Mohist.Server.Workflow.Services.Sessions` 声明

### Requirement: 行为零变更

迁移 MUST 不改变任何可观测行为：label 键名的字符串值（如 `mohist.io/project-id`、`mohist.io/source-kind` 等）、对外 DTO 的字段形状、API 响应契约、查询结果均 SHALL 与迁移前完全一致。

#### Scenario: 标签键名值保持
- **WHEN** 读取迁移后的 `AgentSessionQueryMetadataKeys`
- **THEN** 各常量的字符串值（`mohist.io/project-id`、`mohist.io/issue-number`、`mohist.io/source-kind`、`mohist.io/source-id`、`mohist.io/session-name`、`mohist.io/work-id`、`mohist.io/work-type`、`mohist.io/stage`、`mohist.io/title`）与迁移前逐字一致

#### Scenario: DTO 与 API 契约保持
- **WHEN** 迁移后的 `AgentSessionReadModels` 对外 DTO 被 Api 路由序列化返回
- **THEN** DTO 的字段定义、JSON 形状与迁移前一致，既有 Session 相关 API 响应不发生任何结构变化

### Requirement: 编译与 spec 回归全绿

本次变更 SHALL 保持项目以 `TreatWarningsAsErrors` 编译通过（以此兜底拦截任何遗漏的 namespace 引用）。所有既有 Session 相关 spec（约 29 个）与 DI 注册检查 spec（`MigratedServicesRegistrationSpecs`）SHALL 在迁移后全部通过，无回归。

#### Scenario: 编译拦截遗漏引用
- **WHEN** 执行 server 构建（`dotnet build`，`TreatWarningsAsErrors` 生效）
- **THEN** 构建成功，无任何未解析的 namespace / 类型引用告警或错误

#### Scenario: Session spec 与 DI 注册 spec 无回归
- **WHEN** 运行 server 测试套件
- **THEN** 既有 Session 相关 spec 与 `MigratedServicesRegistrationSpecs` 全部通过，且迁移文件所注册的服务在 DI 容器中仍被正确解析
