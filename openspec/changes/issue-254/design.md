## Context

Issue #254（epic #22 代码复杂度热点治理的低风险起点）要求按"变更原因"拆分 CLI 中三个高复杂度大文件。三者都属"按子命令/按 case 堆叠"反模式，单文件圈复杂度居 cli 包前五。

**当前状态（基于代码清点）：**

1. **`MohistCliCommands.Update.cs`（1717 行）**——单文件塞了 4 个顶层类型 + 嵌套类型。核心 god class `SourceCodeUpdater`（L188–L1683，~1495 行）的构造器注入 **12 个依赖**（`TextWriter ×2`、`IServiceInstaller`、`ICommandExecutor`、`IFileSystem`、`IEnvironmentVariableProvider`、`HttpClient`、两个 `TimeSpan` 超时、两个 `Func<string?>`、`unitDir`）。它混合了四类职责：stage 编排、运行时一致性校验（5 个 `internal Check*Async`：CLI binary / server identity / web assets / runner connection / managed skill assets，L629–L836）、服务就绪探测（`WaitForServerReady*` / `CheckServerReadyOnce*`，L1465–L1620）、runner 刷新结果记录（`RunnerRefreshOutcome` 层级 L1684+、`VerifyRunnerRuntimeAsync` L1237）。**关键有利条件**：每运行态状态全在 `UpdateContext`（L137，参数传递，非实例字段），且 5 个校验已是 internal 可测单元——形成天然提取 seam。

2. **`InfoCollector.cs`（1690 行）**——单 sealed class 混了四类职责：信息采集（`CollectAsync` / `Get*Async`）、输出渲染（`RenderDefault` / `RenderVerbose` / `RenderJson` + 全部 `Build*Json` / `Build*Line`）、systemd 单元解析（`ParseSystemdShow` / `ParseSystemdUnit` / `ParseSystemdTimestamp` / `TryParseUptimeToSeconds` / `FormatUptime` / `BuildStatusFromProperties`）、路径/进程启发式（`ResolveSourcePath` / `ExtractProjectPath` / `TokenizeExecStart` 等，L1494–L1654）。

3. **`TableRenderer.cs`（852 行）**——职责单一（JSON→表格），但有 18 个并列的 `Render*` 实体分支共享表格写入/JSON 取值/截断基础设施。

**命令接线：** `UpdateCommands.Build` 从 DI 取 `SourceCodeUpdater`，调用 4 个公共入口（`UpdateAllAsync` / `UpdateCliAsync` / `UpdateServerAsync` / `UpdateRunnerAsync`）；`InfoCommands.Build` 从 DI 取 `InfoCollector`，调用 `CollectAsync` 后再调用 `collector.RenderJson/RenderDefault/RenderVerbose`。DI 注册在 `Program.cs` L20/L30/L33。

**测试缝隙：** 测试均为命令级集成 spec（`MohistCliCommands.RunAsync(...)` 全流程），覆盖 Agent/Epic/IssueLabel/IssueTemplate/LabelCatalog/Project/Repository——这些间接守护 TableRenderer 的表格输出（断言 output 字符串）。**`mo update` 与 `mo info` 无任何直接测试**；5 个运行时校验、就绪探测、systemd 解析、渲染器均无直接单元测试守护。`InternalsVisibleTo("Mohist.Cli.Tests")` 已启用，提取出的 internal 协作类可直接补单测。

**约束：** 所有 CLI 对外行为（输出、参数、退出码、交互）逐字节不变；无新增/删除子命令或依赖；.NET 11 + `TreatWarningsAsErrors`（C# 侧 lint）。

## Goals / Non-Goals

**Goals:**
- 三个目标文件各自只承担单一变更原因；单文件圈复杂度显著下降，三者均脱离 cli 包前五。
- 更新模块提取出 `RuntimeConsistencyValidator` 与 `ServiceReadinessProbe` 两个窄依赖协作类；facade 构造器注入数显著低于 12。
- 表格渲染器按实体域 partial 拆分，核心 partial 只保留分发与共享基础设施。
- 信息采集器、渲染器、systemd 解析器分离为独立类型。
- 所有现有 CLI 命令测试通过；输出/参数/退出码逐字节一致。

**Non-Goals:**
- 不改变任何 CLI 对外行为；不新增/删除子命令或依赖。
- 不重写更新流水线状态机语义（只搬实现，不改流转）。
- 不做性能优化。
- 不改 server / runner / web。

## Decisions

### 决策 1 — 更新模块：提取 2 个协作类，facade 只保留编排

**做法：** 在新 `Update/` 子目录下新增两个 internal 协作类，把方法从 `SourceCodeUpdater` 机械搬移：

| 新类型 | 搬入的方法 | 依赖（仅这些） |
|---|---|---|
| `Update/RuntimeConsistencyValidator.cs` | `CheckCliBinaryAsync`、`CheckServerIdentityAsync`、`CheckWebAssetsAsync`、`CheckRunnerConnectionAsync`、`CheckManagedSkillAssetsAsync`（L629–L836） | http、`ICommandExecutor`、`IFileSystem`、`IEnvironmentVariableProvider`、`TextWriter`（输出） |
| `Update/ServiceReadinessProbe.cs` | `WaitForServerReadyWithProgressAsync`、`WaitForServerReadyAsync`、`CheckServerReadyOnceWithReasonAsync`、`CheckServerReadyOnceAsync`（L1465–L1620）+ `ServerReadinessResult`/`ReadinessProbeState` 记录 | http、`TextWriter`（进度输出） |

`SourceCodeUpdater`（facade）保留：4 个公共入口、stage 编排（`UpdateAllAsync` / `RunStageMachineAsync` / `FinalizeAfterServerAsync` / 各 `*StageAsync`）、`Finalize*` / `ResolveOutcomeStatus`、`BuildAndRestartServerAsync`、`ResolveRepoRoot`/`ResolveCliPathAsync`/`RuntimeIdentifier`/`ResolveManagedSkillAssetRoot`、`Update*Async` 公共入口。它构造并持有上述两个协作类，stage 体内对校验/探测的调用改为委托。`UpdateContext`（L137）原样保留为独立文件——它已是天然的"每运行态状态袋"，无需改动。

`RunnerRefreshOutcome` 抽象记录层级（L1684）+ `VerifyRunnerRuntimeAsync` / `VerifyRunnerDistManifestAsync` / `ReadRunnerIdentityView` 移到独立文件 `Update/RunnerRefreshOutcome.cs`（仅记录层级独立成文件，校验/刷新逻辑归属按"变更原因"判定：runner identity 校验属运行时校验范畴，留在 facade 编排侧调用，记录层级独立）。

**facade 构造器：** 为最小化 DI 面变更、且让 facade 依赖面真正收窄，facade 构造器**不再平铺 12 个原始依赖**，改为接收"facade 自身所需 + 两个协作类所需"的并集仍然由 facade 构造（保持 `Program.cs` 单点注册），但 facade 内部把校验/探测的依赖**只**注入对应协作类。替代方案 B（把两个协作类也注册进 DI、facade 构造器收窄到"少数 collaborator + 输出"）依赖面更窄但需改 `Program.cs` 注册；**选择 B**——它真正实现"facade 依赖数显著下降"的目标，且 `Program.cs` 改动局限在新增 2 行 `AddSingleton`。

**理由（vs partial 拆 god class）：** 提取协作类降低的是耦合（facade 不再持有校验/探测的全部依赖），partial 只降文件大小不降耦合。`UpdateContext` 参数传递态使提取机械低风险：被提取方签名只依赖它实际用到的依赖，调用方 facade 仍传 `UpdateContext`。

### 决策 2 — 表格渲染器：partial 按实体域拆分（partial 的正确用法）

**做法：** 把 `TableRenderer` 拆为同一 sealed partial class 的多个文件，共享 `TextWriter _out` / `_activeProjectId` / 常量与基础设施方法：

| 文件 | 内容 |
|---|---|
| `TableRenderer.cs`（核心） | `Render` 分发入口（L21）、`WriteTable`（L798）、`AsArray`/`StringOf`/`BoolOf`/`NumberOf`（L807–L840）、`Truncate`（L842）、常量与字段 |
| `TableRenderer.Issues.cs` | `RenderIssueList`/`RenderIssueShow`、`FormatIssueState`、`FormatLabels`、`RenderWorkflowStatus`、`RenderDeliveryFailure`、`RenderSessions`、`RenderFeedbackList`/`RenderFeedbackShow`、`RenderIssueTemplateList`/`RenderIssueTemplateShow`、`WriteBody`、`RenderLabelList`（最大簇） |
| `TableRenderer.Runners.cs` | `RenderRepoList` |
| `TableRenderer.Epics.cs` | `RenderEpicList`/`RenderEpicShow`/`RenderEpicMembership` |
| `TableRenderer.Entities.cs` | `RenderProjectList`/`RenderProjectShow`、`RenderAgentList`/`RenderAgentShow`（瘦小 peer 合收） |

**理由（vs 提取协作类）：** 这里 partial 是**正确**工具，而非 god-class 拆分的反模式。判定依据：①单一职责（JSON→表格）；②多个并列 peer case（18 个实体渲染分支）；③共享同一套基础设施（表格写入、JSON 取值、截断、`_out`/`_activeProjectId` 字段）；④无分歧依赖（所有分支只需 `TextWriter` + `JsonNode`）。提取协作类会迫使每个实体簇复制基础设施或反向依赖核心——不如 partial 共享。注意：`RenderRepoList` 单独成簇文件是因它属 runner/repo 域；若过小可在 review 时合入 Entities。

### 决策 3 — 信息模块：提取渲染器 + systemd 解析器

**做法：** 拆为三个独立类型：

| 类型 | 内容 | 依赖 |
|---|---|---|
| `InfoCollector`（保留瘦身） | `CollectAsync`/`CollectVerboseAsync`、全部 `Get*Async` 采集、`CheckServerConnectivityAsync`、`InspectSourceAsync`、`ComputeDiskUsageAsync`、`IsSystemdAvailable`、`TryGetBinaryPath`、路径/进程启发式（`ResolveSourcePath`/`ExtractProjectPath`/`TokenizeExecStart` 等，作私有静态保留）、`WithTimeout`/`SafeAsync` | `IFileSystem`、`ICommandExecutor`、`IEnvironmentVariableProvider`、`MohistCliApi`、`SkillAssetService` |
| `InfoRenderer.cs`（新增） | `RenderDefault`/`RenderVerbose`/`RenderJson`、全部 `Build*Json`、`WriteIndentedList`、全部 `Build*Line`、`CompactJsonOptions`、`BuildSkillLines`/`BuildEnvVarLines`/`BuildDiskCategoryLines`/`BuildOriginUrl`、`AttachConnectivity` | 仅 `TextWriter` + `InfoResult`/`InfoVerbose` |
| `SystemdUnitParser.cs`（新增静态） | `ParseSystemdShow`/`ParseSystemdValue`/`ParseSystemdUnit`/`ParseSystemdEnvironment`、`TokenizeEnvironmentAssignments`、`ParseSystemdTimestamp`/`NormalizeTimestampForParsing`/`TimestampRegex`、`TryParseUptimeToSeconds`、`FormatUptime`、`TryGetUptimeFromProc`/`ParseStartTimeFromProcStat`、`BuildStatusFromProperties`、`SystemdUnitFields` 记录、常量 `ServerUnit`/`RunnerUnit`/`ShowProperties`/`NotRunning` 等 | 无（静态） |

**`InfoCommands.Build` 接线调整：** 当前 `InfoCommands` 在 `collector` 上调 `RenderJson/RenderDefault/RenderVerbose`。提取后改为从 DI 取 `InfoRenderer` 并调用之（`provider.GetRequiredService<InfoRenderer>()`），并在 `Program.cs` 注册 `InfoRenderer`。`InfoCollector` 不再暴露任何 `Render*`。替代方案（collector 保留 thin forwarding 方法）被否——它抵消提取收益、留下双入口。

### 决策 4 — 验证策略

- **TableRenderer 输出**：现有命令级 spec（Epic/Issue/Project/Agent/...）已断言 output 字符串，是 byte-level 守护。拆 partial 前后这些测试必须全绿。
- **C# 编译守护**：`TreatWarningsAsErrors` + 移动方法时的签名一致性，由编译器强制。
- **提取协作类的直接单测**：因 update/info 现无直接测试，对 `RuntimeConsistencyValidator`（5 个 check）、`SystemdUnitParser`（解析纯函数）、`InfoRenderer`（渲染纯函数）补**最小 internal 单测**（`InternalsVisibleTo` 已启用），锁定提取前后的输入→输出契约。`ServiceReadinessProbe` 涉及真实 http 轮询，单测性价比低，依赖编译守护 + 行为不变承诺。

## Risks / Trade-offs

- **[update/info 内部无直接测试 → 提取引入回归不可见]** → 对提取出的纯函数协作类（校验器/systemd 解析器/渲染器）补 internal 单测；`ServiceReadinessProbe` 依赖编译守护。这是本次最大的风险敞口。
- **[facade 构造器形状变化打破 `Program.cs` L20 的 `new SourceCodeUpdater(...)`]** → 决策 1 选方案 B：同步改 `Program.cs` 注册（新增协作类的 `AddSingleton` + 收窄 facade 构造参数），改动局限、可编译验证。
- **[TableRenderer 用 partial 被误读为"拆 god class"反模式]** → 在核心 partial 文件顶部以注释声明 partial 的正当理由（单一职责 + peer cases + 共享基础设施），供后续维护者判断。
- **[`InfoCommands` 改为依赖 `InfoRenderer` 是 internal 接线变更]** → 行为（输出）不变，由命令级 spec 间接守护；无对外影响。
- **[提取后 `SourceCodeUpdater` 仍持有协作类所需依赖的构造责任]** → 方案 B 下 facade 构造器接收已注册的协作类实例，不再平铺原始依赖；facade 只剩"orchestrator + 协作类 + 输出"。
- **[scc 复杂度排名目标可能因 cli 包其他大文件而未必达成]** → 拆分后用 `scc` 实测验证；若个别文件仍居前五，记录于 tasks 阶段，不强行追加非目标范围的拆分。

## Migration Plan

纯内部重构，无 schema/API/依赖变更，无部署步骤。按目标文件逐个推进，每步之间 `npm test` + `npm run typecheck -w packages/cli`（等价 dotnet build + 现有测试）必须全绿：

1. **TableRenderer partial 拆分**（最低风险，输出有 spec 守护）→ 拆文件 → 跑命令级测试。
2. **InfoCollector 拆分** → 新增 `SystemdUnitParser`（静态纯函数，先搬+补单测）→ 新增 `InfoRenderer`（搬渲染+补单测）→ 改 `InfoCommands` 接线 + `Program.cs` 注册 → 跑测试。
3. **Update 模块拆分**（最高复杂度）→ 先把 `RunnerRefreshOutcome` 层级独立成文件 → 提取 `RuntimeConsistencyValidator`（搬 5 个 check + 补单测）→ 提取 `ServiceReadinessProbe` → 改 facade 构造器 + `Program.cs` 注册 → 跑测试。
4. **收尾**：用 `scc` 验证三者均脱离 cli 包复杂度前五；记录实测数据。

**回滚：** 任一步骤失败即 `git revert` 该步提交；无数据迁移、无远程影响。整个变更可整体 revert 回退到当前状态。

## Open Questions

1. **`RuntimeConsistencyValidator` / `ServiceReadinessProbe` 是否注册进 DI？** 倾向方案 B（注册进 DI，facade 构造器收窄），但需在 tasks 阶段确认 facade 构造器签名最小化后 `Program.cs` 改动量是否可接受。若 DI 注册过重，回退到方案 A（facade 内部构造协作类，仍收窄实例字段但构造参数不变）。
2. **是否对 `ServiceReadinessProbe` 补 http 桩单测？** 性价比偏低（轮询+超时+进度），但能覆盖最大风险敞口。tasks 阶段定夺；至少为"就绪/未就绪/超时"三条主路径补桩。
3. **`RenderRepoList` 是否合入 `TableRenderer.Entities.cs`？** 单方法成簇文件偏碎，review 时若 Entities 簇仍瘦可合并。
4. **`SourceCodeUpdater` 重命名？** 名字已不准确（现在它是 orchestrator facade，不再"仅更新源码"）。重命名超出本 issue 范围（属命名改进），列为后续；本 issue 保持类名不变以最小化 diff。
