## Why

每次读取 WorkflowRun State，服务都先完整解析 JSON 并探测旧格式；即使 State 已是 canonical，仍支付这次探测。当前库 364 行 WorkflowRun 中有 254 行（约 70%）仍需转换（completed 221、failed 26、stopped 7），其中 `failed` 可 retry / rerun、不是终态，迁移不能把它当死历史处理。代价有三：每次整载读都在 Server 最大的字符串反序列化热点上重复全文档解析（State 平均 325 KB）；约 450 行兼容转换及辅助逻辑把 State 演进同时绑死在当前模型与历史格式上；并且直接违反 `design/workflow/run-state.md` 已定稿的规则——遗留格式迁移是数据库升级时的写入义务，读取路径只处理一种 canonical 格式。当前 writer 已只产生 canonical State、旧格式集合已封闭，可以在 Server 进入服务阶段前一次性收敛。

## What Changes

- **遗留转换移出全部读取入口。** 把 WorkflowRun 遗留 State 转换从所有读取入口移到 Server 启动前的数据库升级；服务阶段只读取 canonical State，读取入口直接按当前模型反序列化，不探测历史字段、不调用转换器。
- **不增加逐行 SchemaVersion。** 数据库初始化继续作为唯一升级入口：EF migration 负责 schema 与能由 SQLite JSON 操作无歧义表达的转换；依赖 Workflow 语义、需要结构比较或拒绝歧义输入的转换由同一初始化流程中的有序、幂等 C# data upgrader 用现有唯一转换规则完成，不在 SQL 里复制实现。
- **data upgrader 先无写入 preflight。** 对全部 State 识别旧格式、生成转换结果，并以当前模型直接反序列化。任一行转换或读取失败时不写库、阻止 Server 进入服务阶段。全部通过后在单个事务中写入所有旧格式行并递增对应 ETag；canonical 行逐字节不变。
- **破坏性重写前生成一致备份。** 用 SQLite online backup 或 `VACUUM INTO` 生成备份（WAL 模式下不把单独复制主 `.db` 视为有效备份），验证可打开且 `PRAGMA integrity_check` 通过。
- **幂等。** 首次成功后再次执行写入数为 0；转换器只保留在冷启动迁移边界。

## Capabilities

- `workflow-run-state-startup-migration`: Server 启动期把遗留 WorkflowRun State 收敛为 canonical 的 data upgrader 行为——无写入 preflight、一致备份与完整性校验、单事务提交并按行递增 ETag、canonical 行逐字节不变、按持久化字节幂等（重复执行写入为 0）、不按 WorkflowRun 生命周期筛选（`failed` 等可恢复非终态同样迁移且恢复语义不变）、任一行无歧义转换或读取失败即阻止 Server 进入服务阶段。
- `canonical-state-read-path`: 所有 WorkflowRun State 读取入口（执行平面 report / dispatch、加载 State 的控制平面查询、reconciler 等）直接按当前模型反序列化 canonical State，不探测历史字段、不调用遗留转换器；遗留转换器只存在于冷启动迁移边界。验收指标是读路径对遗留转换器的调用数为 0。

## Impact

- **Server (`packages/server/src/Mohist.Server/`):**
  - `Infrastructure/Data/Workflow/WorkflowRunStateDataUpgrader.cs` — 启动期 data upgrader（preflight + 一致备份 + 单事务 + 幂等），承载从读路径移出的 `MigrateLegacyWorkflowRunJson` 唯一转换规则。
  - `Infrastructure/Data/Db/DatabaseInitializer.cs` — 在 `MigrateAsync` 之后、其它 upgrader 之前调用 State 升级；升级失败即阻止进入服务阶段。
  - 读取入口移除兼容调用（6 个文件、7 处调用点；`WorkflowRunQuerier` 有两处）：`Infrastructure/Data/Workflow/WorkflowRunStore.cs`（`Deserialize` 直接反序列化）、`Infrastructure/Data/Workflow/WorkflowRunQuerier.cs`、`Workflow/Services/WorkflowQuerier.cs`、`Issue/Services/IssueMetricsQuerier.cs`、`Issue/Services/IssueReadModelLoader.cs`、`AgentOps/Services/ActiveSessionReconciler.cs`。issue 正文的「7 个生产文件」对应这 7 处调用点。
- **Persistence / Migration:** 无 schema 列变化、无新 EF migration；转换在 DB 初始化的 data upgrader 阶段完成，重写持久 State 并按行递增 ETag（**非破坏性到 canonical 行**，破坏性仅作用于旧格式行，由 preflight + 单事务 + 一致备份限定范围）。
- **Design docs (`design/`):** `design/workflow/run-state.md` 已描述目标规则（格式演进与迁移、读路径 canonical-only）；其「现状差距」小节点名本 issue，落地后该差距关闭，正文无需改动。
- **Tests:** `WorkflowRunStateDataUpgraderSpecs`、`WorkflowRunRerunMigrationSpecs`、`WorkflowRunLegacyBindingSpecs` 覆盖 preflight、事务回滚、幂等、canonical no-op、`failed` run 恢复行为与歧义输入拒绝；现有 WorkflowRun 存储 / 查询 / 控制 / 恢复 spec 与 unit 全绿作为安全网。
