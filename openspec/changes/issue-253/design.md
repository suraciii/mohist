# Design — Workflow Work Item Protocol Redesign

## Context

`WorkflowGrain`（`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs`，~1260 行）当前同时承担两类职责：

1. **控制面（应留）**：持有 `WorkflowRun` 一致性边界，裁判下一步工作、推进状态机。
2. **执行面（越层，应迁）**：把 work 渲染成 `WorkDispatch`（`WorkflowDispatchBuilder` + `PrepareWorkAsync`/`MakeDispatchAsync`）、解析 runner 原始 `WorkResult`（`ProcessTaskResultAsync`/`ProcessCheckResultAsync`/`ResolveRepairTasks`）、持有 work 超时定时器（`WorkCompletionTimeout`/`FailTimedOutWorkAsync`/`ArmWorkCompletionTimer`）、接收 runner-lost 通知（`NotifyRunnerLostAsync`/`FailLostRunningTasksAsync`）。

越层已产生真实故障：`WorkCompletionTimeout`（默认 30min，`WorkflowGrainOptions.cs:7`）是无视进度的盲墙钟，正是 issue 251 work-timeout 误杀的源头。runner 状态经 `NotifyRunnerLostAsync` 跨进 grain，扩大 blast radius。

当前协议（`IWorkflowGrain.cs`）：
- 出方向：`PollWorkAsync(runnerId) → WorkDispatch?` —— 返回的是已渲染好的 dispatch（含 workId、解析变量、prompt、artifacts 序列化串）。
- 入方向：`ReportResultAsync(runnerId, workId, WorkResult)` —— 接收 runner 原始 `WorkResult(Status, Message, Output, ExitCode, ArtifactUploadIds)`。

域模型 `WorkflowWork`（`WorkflowRun.Work.cs`）是 3 变体：`StageInit` / `Task` / `Checks`，其中 `StageInit` 是 grain 内部跃迁却暴露给调用方（`NextWork()` 在 stage 未初始化时返回 `StageInit`，`PrepareWorkAsync` 见到 `stage-init` 时调 `InitializeStage` 后递归取下一个 work）。

`RunnerGrain`（`RunnerGrain.cs`）当前对 workflow work **不记账**：`ReportWorkflowResultAsync`（:187）是纯 relay，直接转发给 `IWorkflowGrain`；`_agentJobs` 字典只记 agent-job。runner-lost 检测是 grain 定时器 + 内存 `_lastHeartbeat`（:22,:34），silo 重启即失效。

约束：
- **grain 是一致性边界**：`WorkflowRun` 查询搬不走（会丢强一致），只能提 grain 内组合的读模型。
- **pull 模型已落地**：`PollAsync` → store 查询 → `PollWorkAsync`；`RequeueWorkflowIdAsync` 已是 no-op（:616）。
- **不改变状态机语义**：状态/流转/审批不变，只改对外协议形状与翻译位置。

权威设计见 `design/workflow/scheduling.md`（Model / Interfaces / Work State Machine / Supervision / Recovery 段已按本设计更新）。

## Goals / Non-Goals

**Goals:**
- 把 `WorkflowGrain` 收敛为"只对外公开 work item 协议的状态裁判"：出方向返回域语义 work item（声明/模板，不含 dispatch 信息），入方向接收域结果。
- work item ↔ dispatch/result 的翻译迁到调用方（`RunnerGrain` 组合的 translator）。
- 删除控制面越界的监督：work 超时定时器、runner-lost 通知路径；WorkflowGrain 零定时器、零 runner 概念。
- stage-init eager 化：`StageStarted` ⟹ 该 stage 已 init，`WorkflowWork.StageInit` 变体删除。
- `RunnerGrain` 持 outstanding-work 集，runner 丢失时经正常 report 通道合成 `failed`。
- grain 零处引用 `WorkflowDefinition`；`profileManager` 提供窄 API，整模板选择封装其内部。

**Non-Goals（issue 已声明）:**
- 不改状态机状态/流转/审批语义。
- 不改 grain 并发/激活/持久化语义。
- 不引入第二个持有运行态权威的 grain。
- 不做 runner 永久丢失的自动重分配。
- 不做性能优化。
- runner-loss 检测 reminder 化属独立健壮性 bug，可单开 follow-up（本设计给出接入点，但不强制本轮实现）。

## Decisions

### D1. 出方向协议：`PollWorkAsync` 返回域 `WorkItem?`

`IWorkflowGrain.PollWorkAsync(string runnerId)` 的返回类型从 `WorkDispatch?` 改为 `WorkItem?`。新建域 record `WorkItem`（两个变体），**与 runner TS 的 `WorkItem` 镜像同一形状**（见 D7）。

```csharp
// 控制面域语义协议（新建，放 Workflow/Domain/Run/）
public abstract record WorkItem(string Stage);
public sealed record TaskWorkItem(
    string Stage, string Id, string Title, string? Uses,
    Dictionary<string,JsonElement?>? With,        // 未解析模板
    TaskArtifactCapture? Artifacts,
    Dictionary<string,string>? SetVars) : WorkItem(Stage);
public sealed record ChecksWorkItem(string Stage, List<CheckItem> Items) : WorkItem(Stage);
```

**grain 内职责调整**：`PollWorkAsync` 仍负责 `StartTask`（把 task 标 Running、写 `WorkDelivery`）——因为这是**一致性边界的写**（grain 独占态）。`StartTask` 产出的 workId 随 work item 返回（work item 的 `Id` 即 task run id / checks workId）。**变量解析、prompt 加载、with 展开、artifacts 序列化全部不在 grain**。

**替代方案**：返回 dispatch 让调用方自己剥离 —— 否决，因 dispatch 已含解析值，违反"协议是 work item 不是 dispatch"原则。

### D2. 入方向协议：`ReportResultAsync` 改收域结果

`IWorkflowGrain.ReportResultAsync(string runnerId, string workId, WorkResult result)` 改为收域结果。新建：

```csharp
public sealed record TaskOutcome(string WorkId, OutcomeStatus Status, string? Output,
    IReadOnlyList<ArtifactRef>? Artifacts, string? Detail = null);
public sealed record CheckOutcome(string Stage, IReadOnlyList<CheckResult> Results);
public enum OutcomeStatus { Passed, Failed }
```

**结果只有 Passed/Failed**：超时、runner-lost 都是 `Failed` + `Detail`（如 `"work-timeout"`、`"runner-lost"`），不是独立状态。这使 `WorkflowGrain` 收到的合成失败与普通失败无异（满足 D5）。

**artifact 绑定归属（待拍板项已定）**：倾向调用方绑定后把 `ArtifactRef` 放进 `TaskOutcome`。当前 `_artifactBindService` 绑定逻辑（`BindArtifactUploadsAsync` :1018）在 grain 内做"上传 id → 校验 → 记录事件"。绑定是"为执行准备产物引用"的执行面关注 → 迁调用方。grain 只消费 `TaskOutcome.Artifacts` 记录到 `WorkflowArtifactRecorded` 事件。**实施时确认**：若绑定校验强依赖 grain 独占态（当前用 `ResolveBindVariablesAsync` 取 stage vars，但那来自 profileManager，非独占），则外迁无障碍。

### D3. stage-init eager 化（删 `WorkflowWork.StageInit`）

**机制**（已定，见 issue）：
1. 域 `Advance()`（`WorkflowRun.Stage.cs:38`）进入下一 stage 时产出 `StageStarted`（:64）—— **不改**。
2. 域 `Start()`（`WorkflowRun.Lifecycle.cs:64`）首个 stage 也产出 `StageStarted` —— **不改**。
3. **新增**：grain 在 `CommitAsync` 前加一个统一步骤 `InitializeFreshStagesAsync`：扫描待提交事件，凡遇 `StageStarted(stageId)` → 调 `_profileManager.LoadStageSpecsAsync(runId, stageId)` 取 fresh def → 调 `_run.InitializeStage(tasks, checks)` → init 事件并入**同一次提交**。

**关键不变量**：`StageStarted` ⟹ 该 stage 已 init。`NextWork()`（`WorkflowRun.Work.cs:33`）的 `if (!current.Initialized) return StageInit(...)` 分支删除——因为到达 `NextWork` 时 stage 必已 init。

**替换 D1 后的 `PrepareWorkAsync` 的 `case "stage-init"` 递归分支（:628）整体删除**。

**取舍**：profile 结构变更只对新进入的 stage 生效（init 时 fresh load），值层（with/variables）本就在 dispatch 时由调用方 fresh 解析。这是"hot reload per stage-enter"语义，与现状等价。

### D4. 翻译层外迁到 `RunnerGrain` 一侧

新建 `WorkflowItemTranslator`（scoped service，组合 `WorkflowProfileManager` + 原 `WorkflowDispatchBuilder` 的渲染逻辑）：

- **出方向**：`TranslateAsync(WorkItem item, string workflowRunId) → WorkDispatch`。承接现 `WorkflowDispatchBuilder.BuildAsync`（payload 装配、`ResolveLayeredVariablesAsync`、`LoadPromptsAsync`、`ExpandTaskWith`、`MergeTaskOutputsIntoPayload`）+ `MakeChecksDispatchAsync`。输入来自 profileManager/projection，不依赖 grain 独占态。
- **入方向**：`TranslateResultAsync(WorkItemResult runnerResult, WorkItem item) → TaskOutcome | CheckOutcome`。承接现 `ProcessCheckResultAsync` 的 `ParseCheckResults`（runner 格式→域）、failure-recovery 的"runner 格式→域"解析。

**注意**：`ResolveRepairTasks`（:1096，决定 check 失败后插入哪些 repair task）是**控制面裁判**（读 stage def + repair limit + 修改 run）→ **留在 grain**。调用方只翻译格式，不决定 repair。

**接入点**：`RunnerGrain.PollAsync`（:132）拉到 `WorkItem` 后调 translator 渲染成 `WorkDispatch` 再返给 runner 进程；`ReportWorkflowResultAsync`（:187）收 runner `WorkItemResult` 后调 translator 翻成域结果再调 `IWorkflowGrain.ReportResultAsync`。

### D5. 监督分层：删控制面越界，加执行面兜底

**删除（`WorkflowGrain`）**：
- `WorkflowGrainOptions.WorkCompletionTimeout`（整文件删，`WorkflowGrainOptions.cs`）。
- `FailTimedOutWorkAsync`（:817）、`ArmWorkCompletionTimer`（:769）、`WorkCompletionDueTime`（:784）、`ActiveWorkStartedAt`（:798）、`OnWorkCompletionTimerAsync`（:810）、`_workCompletionTimer`（:45）。
- `ReceiveReminder`/`EnsureWorkHeartbeatAsync`/`DisableWorkHeartbeatAsync`/`_workHeartbeatReminder`/`_heartbeatEnsuredThisCommit`/`IRemindable`——整个 heartbeat reminder 机制随 work 超时删除而消失（`On` 里所有 `EnsureWorkHeartbeatAsync` 分支 moot）。
- `IWorkflowGrain.NotifyRunnerLostAsync`（:25）、`WorkflowGrain.NotifyRunnerLostAsync`（:451）、`FailLostRunningTasksAsync`（:747）、域 `WorkflowRun.FailTaskForRunnerLost`。

**新增（`RunnerGrain`）**：
- `_outstandingWorkflowWorks` 字典（镜像 `_agentJobs` 记账模式，覆盖 workflow work）。在 `PollAsync`/`PollAssignedOrAssignableWorkflowAsync` 拉到 workflow work 时记账，在 `ReportWorkflowResultAsync` 完成时移除。**当前 `ReportWorkflowResultAsync` 是纯 relay 不记账（:187）——需补**。
- `NotifyTrackedWorkflowRunnersLostAsync`（:355）改：不再调 `NotifyRunnerLostAsync`，改为遍历 outstanding 集合，对每个 work 调 `ReportWorkflowResultAsync(workflowRunId, workId, synthesizedFailedResult)`，合成 `TaskOutcome(WorkId, Failed, Detail:"runner-lost")` 经 translator 包装后上报。WorkflowGrain 收到的与普通失败无异。

**runner-loss 检测 reminder 化（独立 follow-up）**：当前 `CheckHeartbeatAsync`（:335）是 grain 定时器 + 内存 `_lastHeartbeat`，silo 重启即失效。接入点明确：改用 Orleans `[Reentrant]` grain + persistent reminder + 持久化心跳。**本轮接受"已开 follow-up issue 跟踪"作为满足条件**（验收标准已留口）。

### D6. grain 摆脱 `WorkflowDefinition`（profileManager 窄 API）

`WorkflowProfileManager`（`WorkflowProfileManager.cs`）新增三个窄 API，整模板选择级联（`LoadTemplateAsync` 的 issue-custom > issue-ref > project-default > system-default）封装其内部：

```csharp
public Task<StageDefinition> LoadStageSpecsAsync(string runId, string stageId);
public Task<WorkflowStructure> LoadStructureAsync(string runId);   // stage 序列 + approval flags，Create 用
public Task<ApprovalConfig> LoadApprovalConfigAsync(string runId); // RequestChanges 用
```

**grain 零引用 `WorkflowDefinition`**：
- `LoadEffectiveDefinitionAsync`（:135）删除。
- `Start`（:117）：`LoadStructureAsync` 替代 `LoadEffectiveDefinitionAsync` → `WorkflowRun.Create`（Create 只需结构）。
- `RequestChanges`（:200）：`LoadApprovalConfigAsync` 替代 `LoadEffectiveDefinitionAsync().Approval`。
- `InitializeFreshStagesAsync`（D3）：`LoadStageSpecsAsync` 替代。
- `GetSequentialLockResourceAsync`（:606）：`LoadStageSpecsAsync` 取 `LockBehavior`/`Resources`。
- `ProcessCheckResultAsync` 的 stageDef 查找、`TryScheduleRequestedCheckRepairAsync`：`LoadStageSpecsAsync`。

`LoadEffectiveDefinitionAsync` 删除；`WorkflowDefinition` 降为 profileManager 内部细节（级联仍全量跑，热重载保住——每次 LoadStageSpecs 重跑级联）。

**替代方案**：grain 缓存整体 definition —— 否决，会引入"grain 持有的 definition 过期"热重载问题。

### D7. server / runner work item 形状镜像

runner TS `WorkItem`（`packages/runner/src/core/types.ts:23`）当前是 dispatch 反序列化产物（含 `variables`、`with` 已展开）。改为与 server 域 `WorkItem` 镜像（task/checks 变体、声明/模板未解析）。`toWorkItem`（`connection.ts:196`）的渲染逻辑（现从 `WorkDispatchResponse` 构造 `WorkItem`）随协议变更调整——因 server 直接返 work item，`WorkDispatchResponse` 形状本身变为 work item 形状。

**校准 runner 进程侧 liveness**（spec 要求，非协议）：maxDuration 不设成 grain 旧值 20min；quiet 阈值与 maxDuration 分离。这属 runner 配置层，本设计只标注接入点（runner agentConfig 已有 `livenessQuietThresholdMs`/`probeTimeoutMs`，见 issue body）。

### D8. 配套清理

1. **读模型提取**：`GetFeedbackAsync`/`ListFeedbackAsync`/`GetActiveWorkAsync`/`ToSnapshot`（:491–561）提为 grain 内组合的 read model（`WorkflowReadModel`），grain 委托。仍在 grain 进程内（强一致要求），只是组织上的分离。
2. **锁协调迁 bus 订阅**：`On` 里 `StageCompleted/Failed → ReleaseStageLocksAsync`（:1167-1168）改为 eventbus `[Subscription]` handler（顺着 `WorkflowRunStopped` 已走 bus 的路）。grain 的 `On` 不再直接 release lock。
3. **删 backlog**：`RequeueWorkflowIdAsync`（:616，已 no-op）删除，调用点清掉。

## Risks / Trade-offs

- **[Risk] 协议变更 blast radius 跨控制/执行面，回滚成本高（high 风险）** → Mitigation：分阶段提交（先 D3 eager-init + D6 profileManager 窄 API，二者不改协议；再 D1/D2/D4/D5 协议层）；保留现有测试为契约回归基线；runner/server 同 PR 改。
- **[Risk] stage-init eager 化的提交前 init 步骤遗漏某个产出 `StageStarted` 的转换** → Mitigation：单一入口 `InitializeFreshStagesAsync` 在 `CommitAsync` 统一处理；新增不变量测试"任一 `StageStarted` 后 stage 必 Initialized"。
- **[Risk] artifact 绑定外迁后丢失 grain 独占校验** → Mitigation：D2 实施时确认绑定输入是否真无独占态依赖（当前 `ResolveBindVariablesAsync` 走 profileManager，非独占）；若发现依赖，回退为"调用方绑定引用 + grain 复核"。
- **[Risk] runner-loss 合成失败与真实失败混淆诊断** → Mitigation：`Detail` 字段保留原因（`runner-lost`），日志可区分；协议层对 grain 透明（这是设计目标，非缺陷）。
- **[Risk] runner-loss 检测本轮不 reminder 化，silo 重启 + runner 永久消失时无人触发 closeout** → Mitigation：单开 follow-up issue；当前 grain 定时器检测对单 silo 会话期内仍有效。诚实接受：这是已知缺口，非本轮范围。
- **[Trade-off] profileManager 级联仍全量跑（每次 LoadStageSpecs 重跑 issue-custom>...>system-default）** → 诚实代价：省的是 grain 对 `WorkflowDefinition` 的概念耦合，不是 DB 查询。与现状频率相当。
- **[Trade-off] work item 协议使 runner 侧需新增 translator 逻辑** → 换来 layer 合规与 issue 251 类误杀根除，值得。

## Migration Plan

单仓库、处于积极开发、无需版本兼容（AGENTS.md），故无在线迁移。部署顺序：

1. **域层先行**（D3 + D6 子步骤）：`InitializeStage` eager 化、`StageInit` 变体删除、profileManager 窄 API、grain 零引用 `WorkflowDefinition`。此阶段协议（`PollWorkAsync`/`ReportResultAsync`）**暂不改签名**，grain 内部仍渲染 dispatch，但 stage-init 不再走 `PrepareWorkAsync` 递归。跑全量 workflow 测试。
2. **协议层**（D1 + D2 + D4）：`PollWorkAsync` 返回 `WorkItem?`、`ReportResultAsync` 收域结果、translator 外迁到 `RunnerGrain`。同步改 runner TS（D7）。同步改所有 `PollWorkAnyAsync` 测试 helper（`WorkflowGrainSpecs.cs:212` 等大量调用点）。
3. **监督层**（D5）：删 grain 超时/runner-lost 路径；加 `RunnerGrain` outstanding 记账与合成失败。删 `RunnerFailureSpecs` 里 `NotifyRunnerLostAsync` 相关用例，改为"经 report 通道合成失败"用例。
4. **清理**（D8）：读模型提取、锁迁 bus、删 backlog。

**回滚**：因 high 风险，每阶段独立提交。若协议层（阶段 2）出问题，回滚该提交即可恢复 dispatch 协议；阶段 1 的域层改动（eager-init）本身是行为保持的，可独立留存。

## Open Questions

- **artifact 绑定最终归属**（D2）：倾向调用方绑定后引用入 `TaskOutcome`，但需实施时确认 `_artifactBindService.BindAsync` 无 grain 独占态依赖。当前看依赖的是 profileManager vars（非独占），倾向可外迁。
- **`WorkItem` 域类型与 Orleans 序列化**：作为 grain 接口返回值需 `[GenerateSerializer]`。需确认 `TaskArtifactCapture`/`CheckItem` 等嵌套类型已可序列化，或新建 DTO。
- **runner-loss 检测 reminder 化 follow-up 的 issue 编号**：需在本轮提 follow-up issue 并在验收处引用。
- **`WorkDispatch` 是否完全退役**：agent-job 路径（`AssignAgentJobAsync`/`ReportAgentJobResultAsync`）仍用 `WorkDispatch`。本设计只改 workflow 路径；agent-job 是否同期对齐 work item 协议属可选范围，默认不动（Non-Goal 边界）。
