## Context

WorkflowRun 持久化 State（`WorkflowRuns.State`）历史上经历过若干字段演进：`claim` → `assignment`、`runnerId` → `workerId`（run 级与 task 级）、recovery 结构归一化（`recoveryRemaining` 从声明推导）、`workflowProfileId` 从 `metadata.annotations` 提升为顶层字段、`dispatchActivated` 删除。这些演进长期由读路径兼容承担：`WorkflowRunStore.MigrateLegacyWorkflowRunJson` 在每次整载读时对整条 State 做 `JsonDocument.Parse` 探测并改写。

设计 spec `design/workflow/run-state.md`（「格式演进与迁移」「读写成本规则」）已定稿，规定遗留迁移是数据库升级时的写入义务、读路径只识别一种 canonical 格式。实测 364 行中 254 行仍需转换（completed 221、failed 26、stopped 7）；`failed` 可 retry / rerun、非终态，不能按生命周期筛除。当前 writer 已只产生 canonical State，旧格式集合封闭，可在 Server 进入服务阶段前一次性收敛。

当前架构关键点（决定方案可行性的约束）：

- **冷启动迁移管线**：`DatabaseInitializer.InitializeAsync`（`DatabaseInitializer.cs:11`）顺序执行 EF `MigrateAsync` → 各 data upgrader → Profile 迁移。本 issue 的 State 升级挂入同一条管线，位于 `MigrateAsync` 之后、dispatch 快照外置（#537）与 Profile 迁移之前。
- **读路径绕过 grain**：执行平面（`DispatchService`）、控制平面 status 查询（`WorkflowQuerier`）、reconciler（`ActiveSessionReconciler`）、issue 读模型（`IssueReadModelLoader`、`IssueMetricsQuerier`）直接反序列化 State，不经 grain hop——因此兼容转换曾散布在这些读入口。
- **ETag 是 shadow property**：`WorkflowRuns` 行带 `ETag`（`WorkflowRunStore.StageRunAsync` 每次实际写递增一次）。迁移需在同一事务内按行递增，保持 ETag 语义（乐观并发、status 查询版本化缓存）。
- **#537 已剥离 `dispatchSnapshot`**：本 issue 的转换与快照外置作用在 State JSON 的不同路径上，正交；管线按序执行（#536 先、#537 后），#537 在 canonical State 上操作。

## Goals / Non-Goals

**Goals:**
- 遗留 State 在 Server 接受请求前由启动期 data upgrader 一次性收敛为 canonical；服务阶段读路径只反序列化 canonical。
- 迁移安全：无写入 preflight、一致备份 + 完整性校验、单事务原子提交、按持久化字节幂等、canonical 行逐字节不变、`failed` 等可恢复 run 迁移且恢复语义不变。
- 读路径对遗留转换器的调用数为 0；转换器只保留在冷启动迁移边界。

**Non-Goals:**
- 不改 canonical State 字段形状，不增加 `SchemaVersion` / `StateSchemaVersion` 列。
- 不把 recovery 等领域转换复制为第二套 SQLite SQL 实现。
- 不改任何 WorkflowRun 的业务状态、恢复预算或生命周期语义。
- 不建通用备份产品 / 保留策略 / 数据库迁移框架；dispatch 快照外置与日志读放大由各自 issue（#537 等）承担。

## Decisions

### A. 转换规则整体搬迁，不重写

`MigrateLegacyWorkflowRunJson` 连同其全部辅助逻辑（`BuildLegacyRecoveryPlan`、`WriteRunObject`/`WriteStages`/`WriteTasks`、recovery 声明比对、profile 注解读取）从 `WorkflowRunStore`（读路径）整体迁移到 `WorkflowRunStateDataUpgrader`（冷启动边界），转换算法逐字不变。

**选型理由**：转换器产出已是生产读路径长期验证过的 canonical 形态；重写会引入第二套规则，违背 spec「不在 SQL 与 C# 中复制规则」、并带来字节级回归风险。搬迁使「同一规则」从读路径转移到写义务侧，零语义变更。

**备选（rejected）**：重写为基于当前模型的 round-trip 归一化（先按旧模型反序列化、再按当前模型序列化）。拒绝：旧模型已不存在，重建旧模型等价于维护第二套领域结构；且 round-trip 不能保证逐字节稳定（字段顺序、默认值序列化策略），破坏幂等。

### B. 无逐行 SchemaVersion，结构探测判定候选

不新增 `SchemaVersion` / `StateSchemaVersion` 列。候选判定由结构特征驱动：存在 `claim`、`assignment.runnerId`、task 级 `runnerId`、需归一化的 legacy recovery 结构、或仅有 `metadata.annotations.workflowProfileId` 而无顶层 `workflowProfileId`。转换后用当前模型直接反序列化校验。

**选型理由**：单写入者 + 启动期全库迁移的部署模型不需要每行长期携带格式分支，也不允许多种 State 格式在读路径并存（spec 明文）。结构探测对已封闭的旧格式集合足够且无歧义。

**备选（rejected）**：加 `StateSchemaVersion` 列驱动迁移。拒绝：spec 禁止；单写入者部署下，每行版本号是冗余的读路径分支诱因。

### C. 无写入 preflight → 单事务原子提交

data upgrader 两阶段：

1. **preflight（无写入）**：逐行 `MigrateLegacyWorkflowRunJson` 生成新 State，按 ordinal 字符串比较判定是否变化；对变化行用 `JSON.Deserialize<WorkflowRun>` 反序列化校验。任一行转换异常或反序列化失败 → 收集诊断、抛出阻止启动，**不写任何 State / ETag**。
2. **提交**：preflight 零失败后，生成一致备份（Decision E），在**单个事务**内写入全部候选行并对每行 ETag `CurrentValue = OriginalValue + 1`。候选按 500 一批 fetch 跟踪行，但同属一个事务。

**选型理由**：preflight 把「能否无歧义收敛」与「是否写库」分离，保证未通过校验的库不被动过；单事务使迁移全有或全无，避免半迁移状态进入服务阶段。

**备选（rejected）**：
- *逐行迁移并在 preflight 阶段即写*：非原子，进程中断留下部分迁移；违反「失败不写」。
- *按 WorkflowRun 生命周期分批*（先 terminal、后 active）：引入两套提交语义与跨批次幂等问题；且 `failed` 非终态，仍需迁移，分批无收益。

### D. 按持久化字节幂等；canonical 行逐字节不变

仅当转换输出与原 State 做 ordinal 字符串比较**不同**时才计入候选并写入；相同（已是 canonical）的行原样保留，State 字节与 ETag 均不变。再次执行时所有行均无变化 → 候选 0、写入 0、不创建备份。ETag 递增严格绑定「State 实际改写」。

**选型理由**：字节级幂等是 spec 硬约束，也保证迁移前后读出的 WorkflowRun 状态一致（转换输出即读路径长期使用的同一字节序列）。canonical 行不动使迁移对已是目标态的库为零成本、零风险。

**备选（rejected）**：迁移后无条件 round-trip 重序列化所有行以「规范化」。拒绝：破坏字节幂等，canonical 行 ETag 被无意义推进，影响下游版本化缓存与并发判定。

### E. 一致备份 + 完整性校验，拒绝裸 `.db` 复制

破坏性重写前用 SQLite online backup（`source.BackupDatabase(destination)`）或等价的 `VACUUM INTO` 生成备份；备份库执行 `PRAGMA integrity_check` 须返回 `ok`。WAL 模式下**不**接受只复制主 `.db` 文件（会丢已提交 WAL）。内存库源直接拒绝（无持久化备份意义）。

**选型理由**：WAL 下主 `.db` 不含全部已提交数据；online backup 跨页一致并纳入 WAL。完整性校验在写入前拦截损坏备份。备份是回滚边界，不为其编造逆向转换。

**备选（rejected）**：`File.Copy(mohist.db)`。拒绝：WAL 内容丢失，备份不可恢复。

### F. 不按生命周期筛除；`failed` 同等迁移且恢复语义不变

转换不按 `completed` / `stopped` / `failed` 筛选。`failed` 是可 retry / rerun 的非终态，必须迁移；转换对 recovery 结构的归一化保留其恢复预算与 rerun 行为（由 `WorkflowRunRerunMigrationSpecs` 验证：迁移后 load → rerun 产生新 stage attempt、attempt 递增、旧 task 清空，reload 一致）。

**选型理由**：读路径在迁移后只认 canonical；任何遗留 `failed` 行进入服务阶段都会触发兼容缺失。生命周期筛除会留下「安全前提是历史终态」的隐性假设，而 `failed` 恰恰否决该前提。

**备选（rejected）**：只迁 terminal、把 `failed` 当死历史跳过。拒绝：`failed` 可恢复，跳过使其在迁移后无法 rerun。

### G. DB 初始化是唯一升级入口；recovery 转换不落 SQL

schema 与能由 SQLite JSON 操作无歧义表达的转换走 EF migration；依赖 Workflow 语义、需结构比较或拒绝歧义输入的转换（recovery 归一化、claim/runnerId 改名）由同一初始化流程中的 C# data upgrader 完成，**不在 SQL 里复制规则**。

**选型理由**：recovery 归一化需要跨同 definition 的多 attempt 比对 handlers/task 声明并拒绝歧义，SQLite JSON 表达既不直接也无歧义保障；C# 单点规则可测、可审。

**备选（rejected）**：用 SQLite JSON1 在 migration 里做转换。拒绝：语义复杂、歧义拒绝难以表达，且形成第二套规则。

### H. 读路径直接反序列化；转换器只留冷启动边界

从 `WorkflowRunStore.Deserialize`（及 `WorkflowRunQuerier`、`WorkflowQuerier`、`IssueMetricsQuerier`、`IssueReadModelLoader`、`ActiveSessionReconciler`）移除 `MigrateLegacyWorkflowRunJson` 调用，直接 `JSON.Deserialize<WorkflowRun>`。转换器降级为 `WorkflowRunStateDataUpgrader` 上的成员，仅由 `DatabaseInitializer` 调用。

**选型理由**：迁移已完成是进入服务阶段的前置（Decision C 失败即阻止启动），读路径无需也无法再承担兼容；保留转换器于冷启动边界是为了支持从更旧数据库升级。

## Risks / Trade-offs

- [迁移重写生产 State，误转 / 损坏行] -> preflight 全量无歧义校验 + 一致备份 + 完整性检查 + 单事务原子提交；任一行失败回滚且阻止启动；可从备份恢复。
- [WAL 下备份不一致导致回滚失效] -> 强制 online backup / `VACUUM INTO`，禁止裸 `.db` 复制；备份前 `PRAGMA integrity_check`。
- [迁移与 #537（快照外置）、Profile 迁移同改 State JSON] -> 三者在 `DatabaseInitializer` 中按序执行（State 升级 → 快照外置 → Profile 迁移），作用于正交 JSON 路径；各自幂等、各自 ETag 递增；canonical 行不受影响。
- [`failed` run 迁移后恢复语义变化] -> 由 `WorkflowRunRerunMigrationSpecs` 覆盖 rerun 全链路；recovery 归一化保留预算与 handlers。
- [ETag 推进影响下游版本化缓存 / 并发判定] -> ETag 仅在实际 State 改写时递增一次（Decision D），canonical 行不动；幂等重复执行不再推进。
- [转换器长期保留于冷启动边界，未来手改致字节漂移] -> 字节级幂等 + 现有 spec 测试锁定转换输出；转换器是唯一规则点，无第二份实现可漂移。
- [大库 preflight 全量解析的启动延迟] -> 仅启动一次；候选集合已封闭且不再增长（writer 只产 canonical），后续启动 preflight 命中 0 候选即短路、不创建备份。

## Migration Plan

1. **挂载 upgrader**：`DatabaseInitializer.InitializeAsync` 在 `db.Database.MigrateAsync` 之后调用 `WorkflowRunStateDataUpgrader.UpgradeAsync`（位于其它 upgrader / Profile 迁移之前）。失败抛出 → 阻止进入服务阶段。
2. **搬迁转换器**：`MigrateLegacyWorkflowRunJson` 及辅助逻辑迁至 `WorkflowRunStateDataUpgrader`（Decision A）。
3. **移除读路径兼容**：`WorkflowRunStore.Deserialize` 及其余 5 个读入口改为直接反序列化（Decision H）。
4. **首次部署**：新二进制启动 → EF `MigrateAsync` → State upgrader preflight（候选 254、失败 0）→ 一致备份 + 完整性校验 → 单事务提交（254 行 State 改写、ETag 各 +1，canonical 110 行逐字节不变）→ 快照外置 → Profile 迁移 → 进入服务。

**Rollback**：用 upgrader 生成的 SQLite 备份恢复 `mohist.db`（WAL，须含已提交 WAL；用 online backup，非复制主 `.db`）。恢复后部署旧二进制——旧读路径的 `MigrateLegacyWorkflowRunJson` 对 canonical 行是 no-op、对遗留行仍兼容，因此旧二进制可读新旧两种 State，回滚安全。不为迁移编造逆向转换。

## Open Questions

- **备份保留 / 清理**：迁移成功后生成的 `.workflow-run-state-backup-*.db` 是否清理、保留多久？本 issue 不建保留策略（Non-Goal），倾向由运维手动管理或后续 issue 统一。
- **转换器退役时机**：转换器长期保留于冷启动边界以支持从旧库升级。是否在所有部署均完成迁移后某版本删除？本 issue 不删除（spec 允许其只存于冷启动边界）；删除需以「不再支持从该旧格式升级」为明确前提，另开 issue。
