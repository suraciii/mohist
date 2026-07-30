# WorkflowRun State 持久化

本文规定 WorkflowRun 持久化状态（`WorkflowRuns.State`）的内容边界与读写成本规则。
Dispatch 快照的语义与存储生命周期见 [`task-dispatch.md`](task-dispatch.md) 的
「Dispatch 快照的持久化」。

## State 是什么

State 是单个 WorkflowRun 的持久化权威：run 的当前裁决所需的最小运行事实（status、
assignment、各 Stage/TaskRun 的状态机字段、工作区与仓库引用、任务 output）。Orleans
grain 内存态与 State 一一对应，激活时整体载入，每次状态变更后整体重写。

State **不是**：

- 历史记录——事件历史由事件存储承担，State 不保存可追溯历史；
- dispatch 契约的仓库——dispatch 快照按 [`task-dispatch.md`](task-dispatch.md) 的规则
  单独管理，不进入 State；
- 大内容的存放处——凡可重建或可引用的内容（prompt body、全量 tasks 输出汇总、
  dispatch payload）都不复制进 State。

## 内容边界规则

- 进入 State 的字段必须回答一个裁决问题（调度、重试、恢复、锁、状态展示）；只为
  "以后可能有用"而保留的字段不进入 State。
- 随任务数 / 重试次数线性增长的内容必须按条（TaskRun）计账：单条 TaskRun 只携带自身
  裁决所需的字段，不携带与其他 TaskRun 重复的全量副本（如全套 prompts、全部前序任务
  输出汇总）。
- 被取代或终态的 attempt 不保留 dispatch 快照；恢复链的 attempt 数量由
  [`recovery.md`](recovery.md) 的语义决定，State 不额外设历史上限——体积问题靠
  内容边界解决，不靠截断历史。
- State 单条体积预算：活跃 run 常态应在数百 KB 以内；超过 1 MB 即视为内容边界被破坏，
  按缺陷处理而不是扩容。

## 读写成本规则

- 写：每次状态变更整体重写 State 是既定形态；因此 State 体积直接乘以事件频率等于
  写放大。内容边界（上节）是控制写放大的唯一手段。
- 读：按 run id 的整载读（执行平面 report / dispatch / 日志路径，控制平面 status
  查询）每次承担完整的 State 反序列化成本；调用方不得把整载读当作廉价元数据查询——
  只需 status 等标量字段的查询必须走投影列，不反序列化 State。
- 遗留 JSON 迁移是写入时义务，不是读取时义务：读取路径不做全文档解析式的迁移探测。

## 格式演进与迁移

运行中的 Server 只识别一种 canonical State 格式。`WorkflowRuns` 不增加逐行
`SchemaVersion` / `StateSchemaVersion`：单写入者、启动期全库迁移的部署模型不需要让
每行长期携带格式分支，也不允许多个 State 格式在读路径并存。

State 格式变化是数据库升级的一部分，必须在新 Server 接受请求前完成：

- 数据库初始化是唯一升级入口。EF migration 负责 schema 变化，以及能由 SQLite JSON
  操作直接、无歧义表达的数据转换；依赖 Workflow 语义、需要结构比较或拒绝歧义输入的
  转换由同一初始化流程中的有序、幂等 C# data upgrader 完成，不在 SQL 与 C# 中复制规则。
- 跨多个发布版本升级时，未执行的 EF migrations 与 data upgraders 按既定顺序依次收敛到
  当前格式。每个迁移只负责单向收敛；历史格式支持留在冷启动迁移路径，不进入当前读模型。
- data upgrader 先完成无写入的 preflight：找出全部候选行，以唯一转换规则生成新 State，
  并用当前模型直接反序列化转换结果。任一行无法无歧义转换或无法读取时，不写任何 State，
  Server 启动失败并报告对应 WorkflowRun。
- preflight 全部通过后，在单个数据库事务中写入全部转换结果并递增各行 ETag。转换不按
  WorkflowRun 生命周期筛选：`failed` 是可 retry / rerun 的非终态，也必须在保持语义不变
  的前提下迁移；已经是 canonical 格式的行不得改写。
- data upgrader 必须幂等。写入失败由事务回滚；下次启动从持久数据重新判定并重试，不依赖
  上次进程的内存进度。

破坏性重写生产 State 前必须生成一致的 SQLite 备份并验证可打开。WAL 模式下不得只复制
主 `.db` 文件；使用 SQLite online backup 或 `VACUUM INTO`，保证备份包含已提交的 WAL
内容。恢复以该备份为边界，不为数据迁移编造逆向转换。

迁移完成后，所有读取入口直接按当前模型反序列化 State，不探测历史字段，也不调用转换器。
未完成迁移的数据库不得进入服务阶段。因此验收指标是读路径中的历史转换调用为零，而不是
迁移代码从仓库删除；仍需支持从旧数据库升级时，转换器可以只存在于冷启动迁移边界。

## 现状差距

当前实现与上述规则的差距（按实测，观测时点为 issue #521 的 check 阶段）：

- 单条 State 平均 325 KB、最大 3.6 MB，全表 364 行共 118 MB；超出预算一个数量级，
  主要体积来自按条重复持有的 dispatch 快照（见 task-dispatch 差距小节）。
- 读取路径每次调用都做全量 `JSON.Deserialize<WorkflowRun>`；叠加 `mo run watch` 3s
  轮询与 runner 高频上报，构成 Server LOH 分配风暴（实测 LOH 分配的 95%+ 来自该路径的
  STJ 字符串转码），进程 RSS 峰值达 2 GB。
- 日志路径把整载读当廉价查询：日志上传单次请求对同一 run 做两次整载读（活跃校验与
  publish scope 解析各一次），日志查询每次轮询再做一次整载读，而所需信息只是
  taskId ↔ workId 映射与活跃 work 判定（`TaskLogService`）。
- `WorkflowRunStore.MigrateLegacyWorkflowRunJson` 在每次读取时对整个 State 做
  `JsonDocument.Parse` 迁移探测，违反"迁移是写入时义务"。issue #536 实测 364 行中有
  254 行仍需转换（completed 221、failed 26、stopped 7）；兼容调用分布在 7 个生产文件，
  其中 `failed` 按 WorkflowRun 生命周期不是终态。
- `WorkflowQuerier.GetStatusAsync` 无缓存；行上已有 ETag 列，可用于版本化缓存或
  条件响应。
- 写放大同时作用于 SQLite：`mohist.db` 达 9.2 GB，尚无针对事件 / 转录 / 遥测数据的
  保留策略（现有 `CleanupPolicyOptions` 只覆盖 workspace）。
