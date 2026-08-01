## Context

WorkflowRun State 把每个 task attempt 的完整 `WorkDispatch` 快照内嵌在 `TaskRun.DispatchSnapshot`（`TaskRun.cs:45`）里，随 State 全量序列化、终态后不清理。实测单条 State 平均 325 KB、最大 3.6 MB，约 81% 体积来自这些按 attempt 重复的快照。设计 spec（`design/workflow/task-dispatch.md`「Dispatch 快照的持久化」、`design/workflow/run-state.md`「内容边界规则」）已定稿，要求快照与 State 分离存放、终态即弃。

当前架构关键点（决定方案可行性的约束）：

- **Grain 持有内存态**：`WorkflowGrain` 在 `OnActivateAsync`（`WorkflowGrain.cs:94`）一次性从 `_runStore.LoadAsync` 载入 `_run`，跨调用保持内存，`OnDeactivateAsync` 脏则刷盘。`StoreActiveWorkDispatchAsync`（`:481`）在 grain 内设置 `task.DispatchSnapshot` 并 `SaveRunAsync`。
- **读路径绕过 grain**：`DispatchService.RenderActiveWorkflowAsync`（`DispatchService.cs:221`）通过 `WorkflowRunQuerier.LoadAsync` 直接反序列化 State，从 `activeWork.DispatchSnapshot`（`WorkflowRun.Work.cs:223`）取快照——不经 grain hop。
- **终态不清理**：全仓只有一处赋值 `task.DispatchSnapshot = dispatch`（`WorkflowGrain.cs:496`），终态转换（`CompleteTask`/`FailTask`/`FailTaskForStopped`）从不置空。重试（`RetryFailedTask`，`WorkflowRun.Stage.cs:152`）创建新 attempt（新 `TaskRunId`/`WorkId`），旧 failed attempt 的快照永久留在 State。
- **读路径天然门控**：`CurrentActiveWorkFor`（`WorkflowRun.Work.cs:65`）只返回 `RunningTask`（`Status == Running`）。task 一旦终态，`RunningTask` 为 null，redelivery 自然不再触及该快照——**终态后快照是否物理删除不影响正确性，只影响空间回收**。
- **冷启动迁移管线**：`DatabaseInitializer.InitializeAsync`（`DatabaseInitializer.cs:11`）顺序执行 EF Migrate → `WorkflowRunStateDataUpgrader.UpgradeAsync`（#536 的 legacy 格式 backfill，preflight + SQLite 备份 + 单事务 + 幂等）→ Profile 迁移。#537 的迁移挂入同一条管线。

## Goals / Non-Goals

**Goals:**
- `TaskRun` 不再内嵌 `WorkDispatch`；快照存入独立表，redelivery 按需 PK 查询加载。
- attempt 终态（Completed / Failed）或被重试取代后，快照立即删除。
- 活跃 run 的 State 单条体积回落至数百 KB 量级，不超过 1 MB。
- 升级时在飞 attempt 的快照可用性保持——redelivery 仍逐字重放。
- 现有 dispatch / 重试 / 恢复 / 重投递行为不变（安全网全绿）。

**Non-Goals:**
- 不改快照内容本身（渲染出的 `WorkDispatch` payload 形状不动）。
- 不做读路径查询优化（`TaskLogService` 整载读、`GetStatusAsync` 缓存、投影列）——另走独立 issue。
- 不处理 legacy JSON 格式 backfill（#536 承担）。
- 不建通用快照保留 / 清理产品（orphan 清扫见 Open Questions）。

## Decisions

### A. 独立 `WorkflowDispatchSnapshots` 表（同库同 DbContext）

新增 EF 实体 `WorkflowDispatchSnapshotRow`，挂在 `MohistDbContext` 上，与 `WorkflowRuns` 同库：

```
WorkflowDispatchSnapshots
  WorkflowRunId  text   (PK part 0, MaxLength 50)
  WorkId         text   (PK part 1)
  SnapshotJson   text   (required)
  PK (WorkflowRunId, WorkId)
```

`WorkId` 等同该 attempt 的 `TaskRunId`（`StartTask` 设 `task.WorkId = workId`，`workId` 来自 `task.Id`）。每个 attempt 一个唯一 WorkId，重试产生新 WorkId → 新快照行。

**选型理由**：独立表实现 spec 的「分离存放」；redelivery 走索引 PK 查询（单行），不再反序列化整条 State；快照生命周期与 State 解耦，终态删除是单行 DELETE。

**备选（rejected）**：
- *WorkflowRuns 行上加列*：redelivery 仍整行读，快照列在非 redelivery 路径也被拉入（除非逐查询 projection）；快照生命周期与行耦合，终态清理要么改 State JSON 要么清列，不干净。
- *Orleans grain state*：需独立 storage 配置，grain 已是权威，徒增复杂度。

### B. 读写路径分工不变（grain 写、DispatchService 直读 store）

保持现有架构：写经 grain，读绕过 grain。

- **写**：`WorkflowGrain.StoreActiveWorkDispatchAsync` 校验 active work 后，写入快照 store（首写胜利，见 Decision E）。不再 `task.DispatchSnapshot = dispatch`，不再因快照而 `SaveRunAsync`。
- **读**：`DispatchService.RenderActiveWorkflowAsync`（`DispatchService.cs:242`）改为从快照 store 按 `(workflowRunId, workId)` PK 查询加载，替代从 `activeWork.DispatchSnapshot` 取值。
- **`WorkflowActiveWork` 去掉 `DispatchSnapshot` 字段**：`ActiveTask`（`WorkflowRun.Work.cs:216`）不再返回快照；`ActiveChecks` 本就返回 null，无变化。

redelivery 从「一次整载读（含 N 个快照）」变成「一次 slim State 读 + 一次 PK 快照查」。两者都廉价，总量大幅下降（slim State 无快照，快照查只取当前活跃的一行）。

**备选（rejected）**：redelivery 读也经 grain（grain 持有快照内存缓存）。拒绝：引入额外 grain hop，且 grain 激活时不预载快照（lazy），缓存命中前仍要回源；现有架构刻意让读绕过 grain 以降延迟。

### C. 终态即弃：grain 在终态转换后删除快照（best-effort）

grain 是 task 状态转换的权威。终态转换入口（`ReceiveTaskReportAsync`、`FailActiveWorkAsync`、`RejectActiveWorkDispatchAsync`、`StopAsync` → `AbandonRunningWorkAsync`）在 `CommitAsync`（State 落盘）之后，对刚终态的 workId 调 `_snapshotStore.DeleteAsync(GrainKey, workId)`。

**正确性不依赖删除成功**：`CurrentActiveWorkFor` 只认 `RunningTask`；task 终态后 `RunningTask == null`，redelivery 不再返回该 work，孤儿快照不可见。物理删除是空间回收。

**删除时机**：在 `CommitAsync`/`SaveRunAsync` 之后。若 State 落盘失败（ETag 冲突 → `MarkRunReloadRequired`），跳过删除——task 在持久 State 里仍 Running，快照应保留（grain 重激活后 redelivery 仍可取）。

**重试（supersede）**：`RetryFailedTask` 只在 task 已 Failed 后触发；该 attempt 的快照在 fail 时已删除，重试创建新 attempt（新 WorkId）→ 新快照。supersede 由 fail-time 删除隐式覆盖。

**备选（rejected）**：把快照删除折进 `WorkflowRunStore.SaveAsync` 的事务（与 State 同事务原子删除）。拒绝：store 当前对快照无感知；正确性已由 Running 门控保证，事务耦合不带来正确性收益，只增耦合。若后续需严格无孤儿，可再折进。

### D. 迁移：EF migration 建表 + 冷启动 upgrader 外置活跃快照、剥离全部快照

**EF migration**：创建 `WorkflowDispatchSnapshots` 表（Decision A schema）。`db.Database.MigrateAsync` 先跑。

**冷启动 upgrader**（新步骤，挂在 `DatabaseInitializer` 中 `WorkflowRunStateDataUpgrader.UpgradeAsync` 之后）：

1. **preflight（无写入）**：逐行 `JsonDocument.Parse(State)`，遍历所有 stage/task，找出携带 `dispatchSnapshot` 的 task。对 `Status == Running` 的 task：提取快照 JSON，加入待外置集合；对所有 task：记录剥离后 State。用当前模型反序列化剥离结果，任一行失败则不写、阻止启动。
2. **备份**：沿用 `WorkflowRunStateDataUpgrader.CreateAndVerifyBackupAsync`（SQLite online backup + `PRAGMA integrity_check`）。
3. **单事务写入**：在一个事务内——剥离后的 State 写回各行（ETag 递增一次）；待外置快照 INSERT 进 `WorkflowDispatchSnapshots`（`INSERT OR IGNORE`，防重）。
4. **幂等**：再次执行时 State 已无 `dispatchSnapshot`（无改动、ETag 不动），快照表已无待插（Running task 的快照已在首次外置；若 attempt 已终态，该行在首次 run 后已被 grain 删除）。canonical 行逐字节不变。

**与 #536 的关系**：#536 的 upgrader 转换 legacy 格式字段（claim→assignment、recovery 归一化等），#537 剥离 `dispatchSnapshot`。两者作用在 State JSON 的不同路径上，正交。`DatabaseInitializer` 按序执行（#536 先，#537 后），#537 在 canonical 格式 State 上操作。

**STJ 兼容**：剥离 `DispatchSnapshot` 后，旧 State 行被 `JSON.Deserialize<WorkflowRun>` 加载时，`TaskRun` 已无该属性——STJ 默认忽略未知成员，读安全。新写不再含该字段。

### E. 首写胜利：grain 串行化 + store 语义

`StoreActiveWorkDispatchAsync` 的首写胜利（快照生成后不变、redelivery 逐字重放）由两层保证：

- **grain turn 串行**：Orleans grain 单线程处理 turn；`ClaimAndRenderWorkflowAsync`（首次 claim）与 `RenderActiveWorkflowAsync`（redelivery 补存）都经同一 grain，调用串行。
- **store 层**：先 `LoadAsync(runId, workId)`；非空则返回既有快照（首写胜利）；空则 INSERT。grain 重新激活时内存缓存丢失，store 是真相源——重新激活后 `LoadAsync` 命中已持久快照。

## Risks / Trade-offs

- [迁移误删 / 损坏在飞 attempt 的快照 → redelivery 丢逐字重放] -> preflight 全量校验 + SQLite 备份 + 失败不写阻止启动；迁移可从备份回滚。
- [终态删除失败留下孤儿快照 → 空间累积] -> 孤儿不可见（`RunningTask` 门控）；正确性不受影响。清扫策略见 Open Questions。
- [redelivery 从一次读变两次读（slim State + 快照 PK）] -> slim State 远小于原 fat State（去掉 ~81% 体积），快照 PK 查询单行索引；净分配与延迟大幅下降。
- [#536 与 #537 迁移同管线、同改 State JSON] -> 两个 upgrader 步骤按序执行，作用于正交 JSON 路径；各自幂等、各自 ETag 递增；canonical 行不受影响。
- [grain 重新激活后内存无快照缓存] -> store 是真相源，`LoadAsync` 命中持久快照；首次 dispatch 与 redelivery 补存的「无快照则 TranslateToDispatchAsync 重建」路径不变（既有恢复语义）。
- [`WorkflowActiveWork` 去字段是 record 形状变化] -> Orleans `[GenerateSerializer]` 重建；AGENTS.md 声明无需版本兼容，单写入者部署。

## Migration Plan

1. **EF migration** `AddWorkflowDispatchSnapshots`：建表（Decision A）。随 `db.Database.MigrateAsync` 自动应用。
2. **冷启动 upgrader**：实现 `WorkflowDispatchSnapshotDataUpgrader.ExternalizeAsync`，挂入 `DatabaseInitializer`（#536 upgrader 之后）。
3. **代码切换**：移除 `TaskRun.DispatchSnapshot`；grain 写 / DispatchService 读改走快照 store；终态转换加删除；`WorkflowActiveWork` 去字段。
4. **部署顺序**：新二进制启动 → EF migration 建表 → #536 upgrader → #537 upgrader（外置活跃快照、剥离全部）→ 进入服务。在飞 attempt 的快照在 upgrader 中外置到新表，redelivery 不中断。

**Rollback**：回滚前用 upgrader 生成的 SQLite 备份恢复 `mohist.db`（WAL 模式，须含已提交 WAL；用 online backup 备份，非复制主 `.db`）。恢复后部署旧二进制（旧代码仍认 `TaskRun.DispatchSnapshot`，从 State 读）。不编造逆向转换。

## Open Questions

- **孤儿快照清扫**：终态删除失败会留孤儿（不可见但占空间）。是否在本 issue 加一个启动期清扫（扫描快照表，删除对应 task 已非 Running 的行），还是另开 issue？倾向本 issue 至少加启动期清扫，因为迁移本身已遍历全部行。
- **快照表是否随 run 删除而级联**：`WorkflowRunStore.DeleteAsync` 删 run 行时应同时删该 run 的快照行。需确认是否加 FK `ON DELETE CASCADE` 还是 store 显式删。倾向 store 显式删（与现有 `DeleteAsync` 一致，避免 FK 约束与 computed column 的交互）。
