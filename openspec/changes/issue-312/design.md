## Context

`WorkflowGrain`（`packages/server/src/Mohist.Server/Workflow/Grains/WorkflowGrain.cs`）是 972 行的核心域 grain，`[Reentrant]`，单一持久化 `WorkflowRun` + ETag。命令侧逻辑长期内联：

- **stage-lock 协调簇**：`AcquireStageLocksIfNeededAsync` / `ReleaseCurrentStageLocksAsync` / `ReleaseStageLocksAsync`（也是 `IWorkflowGrain` 公共方法，由 bus 侧 `WorkflowStageLockReleaseHandler` 调用）/ `GetSequentialLockResourceAsync`。不依赖 `_run` 突变，只读 `CurrentStageId`。
- **outcome 处理簇**：`ProcessTaskOutcomeAsync` / `ProcessCheckOutcomeAsync` / `ResolveRepairTasks` / `TryScheduleRequestedCheckRepairAsync` / `ClearExecutableStateAsync` / `MarkTaskRunningAsync` / `MarkChecksRunning` / `ToWorkItemAsync` / `TryBuildActiveWorkItem`。**直接突变**传入的 `WorkflowRun`（`CompleteTask` / `FailTask` / `AddRuntimeTasks` / `StartTask` / `ProcessCheckResults`），且 `MarkTaskRunningAsync` 经 `_sessionHealth.CheckAndEnforceAsync` 触发 commit、`ClearExecutableStateAsync` 经 `SaveRunAsync` 保存 `FailTaskForStopped` 事件。
- **stage 初始化簇**：`InitializeFreshStagesAsync`，在 `CommitAsync` 内、`SaveRunAsync` 之前执行，维持 `StageStarted ⟹ Initialized` 不变量。

既有先例：

- `WorkflowReadModel` —— **composed 内联**（`new WorkflowReadModel(this)`），持有 `WorkflowGrain _owner`，经 internal 访问器（`RunOrNull` / `GetProjectId()` / `GetIssueId()` / `GetIssueNumber()`）读 grain 状态。纯读，不突变。
- `WorkflowSessionHealthService` —— **DI 注入的 `IScopedService`**，方法签名 `(WorkflowRun run, Func<IReadOnlyList<WorkflowEvent>, Task> commitAsync, ...)`：按引用接收可变 `WorkflowRun` + commit 回调。它是 outcome 簇耦合度的下界参照（只**读** run + 回调 commit，不直接突变 run）。

守护套件：`packages/server/tests/Mohist.Server.Tests/Specs/Workflow/Grain/` 下 35 个 spec 文件（~8k 行），作为行为等价性的兜底。约束详见 proposal 与三份 spec；最硬的不变量是「不在 run 突变中途引入新的 async 让出点」与「ETag 冲突时 `SaveRunAsync` 内 `DeactivateOnIdle()` 重载路径」。

## Goals / Non-Goals

**Goals:**

- 把三簇命令侧 helper 抽到与 `WorkflowReadModel` 同区域的独立类型，使 `WorkflowGrain` 本体收敛为 装载/保存/派发/委托。
- 行为逐字等价：`IWorkflowGrain` 签名、`[GenerateSerializer]` record 字段顺序、持久化 JSON blob + ETag、`[Reentrant]` 并发语义、事件发布顺序——全部不变；守护 spec 全部不加修改通过。
- 让抽取可增量验证：三簇可独立落地、独立跑 spec。

**Non-Goals:**

- 不改 `IWorkflowGrain` 接口（任何方法签名或 `[GenerateSerializer]` record 字段顺序）。
- 不改持久化 schema / ETag / 迁移契约。
- 不引入 CQRS 读模型表。
- 不迁出 `PollWorkAsync`（查询签名 + 命令副作用混合）与 `GetAssignedRunnerIdAsync`（读未持久化的 `_lastKnownRunnerId`）。
- 不改 `WorkflowRun` 域突变方法本身（新服务通过传入引用调用它们）。
- 不做性能优化、不碰 `On()` 事件分发死脚手架（#371 范围）。

## Decisions

### D1：三个新类型都用 composed 内联（持有 grain 引用），而非 DI 注入

`WorkflowStageLockCoordinator` / `WorkflowStageInitializer` / `WorkflowOutcomeProcessor` 都在 grain 构造处 `new X(this)`，持有 `WorkflowGrain _owner`，经 internal 访问器访问 grain 状态——**沿用 `WorkflowReadModel` 模式**。

**理由**：

- 三簇都需要 grain 进程内强一致状态（`_run` 引用、`GrainKey`、`GrainFactory`、`_profileManager`、保存路径 `SaveRunAsync`），其中 `SaveRunAsync` 含 ETag 冲突 → `DeactivateOnIdle()` 的重载语义，与 grain 激活生命周期紧耦合，无法干净地外提为 DI 服务。
- outcome 簇还要写 `_lastKnownRunnerId`（grain 基础设施状态），DI 服务不应触及。
- composed 内联保留既有「同步直调、不增加跨进程 hop」的特性，对「不引入新 async 让出点」不变量最友好。

**考虑过的替代**：全部走 `WorkflowSessionHealthService` 的 DI 注入 + `(run, commitAsync)` 签名。否决：`SessionHealthService` 之所以能 DI 是因为它**只读 run + 回调 commit**；outcome 簇要直接突变 run + 经 grain 保存路径保存（`FailTaskForStopped`）+ 写 `_lastKnownRunnerId`，强行 DI 需要把 `SaveRunAsync`、`_lastKnownRunnerId` setter、`GrainFactory` 全部以回调/factory 形式穿过服务边界，参数膨胀且仍绕不开 grain，净增耦合而非减少。

### D2：outcome 簇用「按引用接收可变 `WorkflowRun`」的方法签名表达共享突变契约

`WorkflowOutcomeProcessor` 的公共方法签名形如 `ProcessTaskOutcomeAsync(WorkflowRun run, TaskOutcome outcome, string taskRunId, string workId)`——**显式按引用接收可变 `WorkflowRun`**，而非通过 `_owner.RunOrNull` 隐式取。突变（`run.CompleteTask()` / `run.FailTask()` / `run.AddRuntimeTasks()` / `currentTask.Output = ...` 等）发生在该传入对象上。

**理由**：

- 这是共享突变而非纯委托。把 run 放进签名使「突变对象 = grain 的 `_run`」这一不变量在调用点可见、可审；grain 调用方显式传 `_run!`，返回后 grain 观察到的 `_run` 反映这些写入。
- 与 `WorkflowSessionHealthService` 的 `(WorkflowRun run, ...)` 签名一致，领域内形成统一的「按引用突变 run」表达。

**与 D1 的关系**：处理器仍 composed 内联持有 `_owner`，用于回调 grain 的保存/commit 路径；但 run 经方法参数传入以表达突变契约。两者不冲突——`_owner` 提供「回到 grain 的保存/派发通道」，参数 `run` 提供「被突变的状态对象」。

### D3：保存与事件派发路径留在 grain，新服务经 internal 访问器/回调回到 grain

- `SaveRunAsync` / `SaveRunAsync(events)` / `CommitAsync` **留在 grain**（它们拥有 ETag 冲突 → `DeactivateOnIdle()` 重载 + 事件 `On()` 派发 + `InitializeFreshStagesAsync` 调用时序）。
- 新服务回到 grain 的方式按用途分两种：
  - **保存路径**（`MarkTaskRunningAsync` 的 `SaveRunAsync(events)`、`ClearExecutableStateAsync` 的 `SaveRunAsync(events)`）：经 grain 新增的 internal 访问器（如 `internal Task SaveAsync(IReadOnlyList<WorkflowEvent> events)`），签名与既有 private `SaveRunAsync(events)` 逐字一致，保留 `DeactivateOnIdle()` 重载。
  - **commit 回调**（`MarkTaskRunningAsync` 经 `_sessionHealth.CheckAndEnforceAsync` 触发的 commit）：沿用既有 `events => CommitAsync(events)` 闭包，回调签名 `Func<IReadOnlyList<WorkflowEvent>, Task>` 与 `WorkflowSessionHealthService` 逐字一致（spec 硬要求）。
- `_lastKnownRunnerId`：经 grain 新增的 internal setter 访问器，或由处理器方法返回 runnerId、grain 调用方写入——**实现期二选一**，倾向后者（不让域服务触及基础设施字段），但 `MarkTaskRunningAsync` 在两条分支（已 Running 早返回 / `StartTask` 后）都写，前者更不易遗漏。落地时按可读性择优，spec 不约束此处。

**理由**：保存+ETag 重载是 grain 激活语义的一部分，外提会破坏 `DeactivateOnIdle()` 路径或强迫服务模仿 grain 激活，得不偿失。让新服务「回到 grain 保存」是最小风险路径。

### D4：`IWorkflowGrain.ReleaseStageLocksAsync` 方法体保留在 grain，委托到协调器

`ReleaseStageLocksAsync(string stage, string reason)` 是 `IWorkflowGrain` 公共方法，被 bus 侧 `WorkflowStageLockReleaseHandler`（`grain.ReleaseStageLocksAsync(stage, reason)`）调用。抽取后 grain 保留该方法（签名逐字不变），方法体改为一次对 `WorkflowStageLockCoordinator` 的委托调用。外部调用方零修改。

### D5：抽取顺序 = 风险升序，每步独立验证

1. `WorkflowStageLockCoordinator`（零 `_run` 突变，只读 `CurrentStageId`）——先做。
2. `WorkflowStageInitializer`（只在 `CommitAsync` 内、保存前执行，突变限于 `run.InitializeStage`，事件合并）。
3. `WorkflowOutcomeProcessor`（共享突变 + session health gate + 保存路径，耦合最高）。

每步落地后立即跑全量 `Specs/Workflow/Grain/`，绿了再下一步。outcome 簇内部 `ClearExecutableStateAsync` 依赖协调器（先释放当前 stage 锁），故协调器必须先就位——顺序不可调换。

## Risks / Trade-offs

- `[共享突变被隐藏到 composed 服务] -> Mitigation`：outcome 处理器在传入的 `WorkflowRun` 上直接突变，读者可能误以为是纯查询。缓解：方法签名按 D2 显式接收 `WorkflowRun run`；XML doc 标注「mutates the passed run」；spec 的「传入的是可变引用而非快照」scenario 守护。
- `[保存/commit 回调穿过服务边界，新增间接层] -> Mitigation`：保存路径与 commit 回调签名与既有逐字一致（D3）；不在回调链路插入新 await；守护 spec（`WorkflowRetrySessionHealthGuardSpecs` / `WorkflowRunContextExhaustionBlockSpecs` / `FailureSpecs`）兜底 session health gate 与保存顺序。
- `[抽取过程中误改 async 让出点位置] -> Mitigation`：抽取是「搬移既有 await 到另一类型的方法」，不新增 await；代码审查重点核对「run 突变 → 保存」之间无新增让出；spec 硬约束「抽取 SHALL NOT 在 run 突变中途引入新的 async 让出点」。
- `[outcome 簇耦合度高，回归面大] -> Mitigation`：35 个 spec / ~8k 行守护套件全量跑；outcome 簇对应 `TaskOutputCaptureSpecs` / `CheckRecoverySpecs` / `CheckRetrySpecs` / `FailureSpecs` / `WorkflowArtifactBindingSpecs` 等多个 scenario 直接覆盖。
- `[composed 内联使新类型访问 grain internal 成员，弱化封装] -> Mitigation`：沿用既有 `WorkflowReadModel` 已建立的 internal 访问器惯例（`RunOrNull` / `GetProjectId` 等），新增访问器仅限三簇实际需要；新类型与 grain 同程序集同区域，internal 表面不外泄。
- `[Trade-off]` 选择 composed 内联而非 DI，意味着这三个类型不可复用于 grain 之外、不可独立单测其与 grain 的协作（须通过 spec 套件）。可接受：它们本就是 grain 强一致边界的内部拆分，没有 grain 外复用场景。

## Migration Plan

纯内部重排，**无外部契约变化、无 schema 迁移、无配置变化**。

- **部署**：正常构建（`npm run build` → `dotnet build Mohist.sln`）+ 部署；`mo update server` 受管理重启。
- **验证**：`npm test`（server 全量，含 `Specs/Workflow/Grain/` 35 文件）；server 靠 `TreatWarningsAsErrors` 当 lint。
- **回滚**：`git revert` 对应 commit + 重新部署。无数据回滚需求（持久化字节不变）。
- **分步落地**（与 D5 一致）：每个新类型一个 commit，各自跑全量 spec，任一步红即停在该 commit 不前进。

## Open Questions

- `_lastKnownRunnerId` 写入：D3 列了两种实现选择（internal setter vs. 方法返回 runnerId 由 grain 写），留到实现期按可读性定。spec 不约束。
- `WorkflowStageLockCoordinator.ReleaseCurrentStageLocksAsync` 需要读 `CurrentStageId`：经 `_owner.RunOrNull?.CurrentStageId` 读，还是由 grain 调用方传入 stage id？倾向后者（调用方 `RetryAsync`/`RerunAsync`/`ClearExecutableStateAsync` 都能提供），但实现期确认调用点是否都能无副作用地提供 stage id 后再定。
