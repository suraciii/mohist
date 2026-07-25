## Why

Mohist 当前完整保存所有已接收的 Trace，`otel.db` 会无限增长，最终占满磁盘、拖慢查询或卡住 Server。内置观测默认开启的前提（`design/observability.md`「启用门槛」）是存储上限与过载降级都已生效，而今天这两者都缺失——既没有保留期限，也没有空间上限或自动清理。观测数据本可丢失，但运营者不应被迫停服手工删文件来维持运行；需要一个有界、自恢复的存储轮转，让 Mohist 能在单机长期运行。

## What Changes

- 新增时间预算：Trace 默认最多保留 72 小时，超期的完整 Trace（Trace 头 + 其全部 Span）按有界批次删除；删除可中断并在下一个维护周期继续。
- 新增空间预算：`otel.db`、其 WAL 和 SHM 合计默认最多 1 GiB。达到 90% 高水位开始按完整 Trace 回收，回到 80% 低水位停止；正常运行不超过预算加一个内部写入块。
- 在线不执行 full `VACUUM`：删除腾出的空闲页靠 SQLite 原生复用回收；WAL 用显式 `wal_checkpoint(TRUNCATE)` 维持硬边界，不长时间独占重写数据库文件。
- 写入侧兜底：空间回收跟不上时，停止接收新 Trace，按 OTLP `partial_success` 返回被拒绝的 Span 数，累加过载拒绝计数，并在运行状态中显示降级原因。
- 重启安全：回收水位在重启后从持久化状态安全继续；关闭观测时不运行任何清理，重新启用后接续，无需用户介入。
- 升级路径：既有数据库已超过安全预算时，在停止观测连接后重建空的观测库，记录明确日志和「数据重置」状态原因，不在启动时长时间压住核心服务。
- 配置：把硬编码的 1 GiB 存储预算提升为 `Mohist:Otel` 配置项（保留原默认），并新增 72h 保留期配置项；只暴露这两个 spec 值，不引入其它可调旋钮。
- `OtelOptions.Enabled` 默认保持 `false`：本变更交付「存储上限 + 过载降级」这一半启用门槛；默认开启依赖单独的请求/并发限制 issue（`design/observability.md`「资源预算」）。

## Capabilities

- `otel-trace-retention`: 时间预算（72h）驱动的完整 Trace 批量删除；删除单位是完整 Trace（Trace 头 + 全部 Span），可中断、可继续，时间逻辑走可注入时钟。
- `otel-storage-budget`: 空间预算（1 GiB，含 db + WAL + SHM）的高低水位（90% / 80%）回收；空闲页复用与 WAL 硬边界，在线不执行 full `VACUUM`；重启后从持久化水位安全继续。
- `otel-ingest-admission`: 空间无法及时回收时，在写入侧停止接收新 Trace，OTLP `partial_success` 返回被拒绝的 Span 数，累加过载拒绝计数，并通过运行状态暴露降级原因。
- `otel-storage-recovery`: 启动时对已超安全预算的观测库的恢复路径——停止观测连接、重建空观测库、记录明确日志与「数据重置」状态原因，不在启动时长时间压住核心服务。

## Impact

- **Server 观测**（`packages/server/src/Mohist.Server/Otel/`）：在已预留但未注册的 `IOtelMaintenanceCallback`（`OtelDiagnosticsSampler.cs`）扩展点上实现时间与空间回收；用预算感知的写入准入替换 `AcceptAllIngestProtectionDecision`（`IngestPreparation.cs`）；扩展 `OtelStorageProbe` 复用已有 db+WAL+SHM 测量；为 `OtelDb` 增加有界删除与 `wal_checkpoint(TRUNCATE)`，以及必要时承载持久化水位的本地元数据。
- **配置**（`packages/server/src/Mohist.Server/Otel/OtelOptions.cs`、`MohistServiceRegistration.cs`）：提升 `StorageBudgetBytes` 为配置项、新增 `RetentionMaxAge`；保留 `RuntimeValueRules.StorageBudgetBytes` 当前默认值（1 GiB）不变。
- **状态面**（`OtelStatusDto.cs`、`RuntimeObservabilityContracts.cs`）：新增「存储预算超限」类降级原因（增量式，不改变 off / healthy / degraded 三态契约）；`/otel/api/status` 与 `mo otel status` 自动透出。
- **Schema 契约**：删除走纯 `DELETE FROM traces / spans`，不改 `traces` / `spans` 表与索引名（稳定契约，CLI 直读依赖）；持久化水位放本地元数据，不触碰业务库。
- **测试**（`packages/server/tests/...`）：复用 `InMemoryOtelDb` 与 `FakeTimeProvider`；空间行为用 fake `IOtelStorageProbe` 验证，不依赖真实文件；按 operation 计数验证维护成本不随无关历史增长。
- **不改**：核心业务库 `mohist.db`、`/api/health`、Runner、Web UI、默认 `OtelOptions.Enabled = false`。
