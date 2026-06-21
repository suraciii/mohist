## Context

Mohist Server 当前是一个 ASP.NET Core + Orleans 单进程 daemon，监听单个 HTTP 端口（默认 3456），使用 EF Core + SQLite 持久化主业务数据到 `~/.mohist/mohist.db`。Server 内没有任何 OpenTelemetry / OTLP 相关依赖——这是一个全新的 telemetry 接入面。

关键现状约束（影响设计决策）：

- **单端口路由**：`Program.cs` 通过 `builder.WebHost.UseUrls(...)` 绑定一个端口，所有路由（`/api/*`、SPA fallback、SignalR hubs）都挂在这一个端口上。路由组织为 minimal API + `static class Map*Routes()` 扩展方法，统一注册在 `MohistApiRegistration.MapMohistApi()`。
- **统一响应信封**：所有 `/api/*` 端点返回 `ApiResponse<T>`（`{ success, data, error, code }`）。OTLP 端点必须返回标准 OTLP JSON `{}`，不能套用这个信封。
- **数据访问**：主库使用 `AddDbContextFactory<MohistDbContext>` + EF Core 迁移。连接字符串由 `ResolveSqliteConnectionString()` 解析，支持 `Mohist:DbPath` / `MOHIST_DB_PATH` / 默认 `~/.mohist/mohist.db`。
- **配置体系**：无 `appsettings.json`；全部来自 `~/.mohist/config.jsonc` + 环境变量。Options 类遵循 `public const string SectionName = "Mohist:..."` + `services.Configure<T>(configuration.GetSection(...))` 模式。
- **CLI**：基于 `System.CommandLine` (2.0.2)，每个命令组是一个 `static class Build(MohistCliApi api)` 方法。CLI **不预检** server，而是在 `MohistCliApi` 内 try/catch `HttpRequestException` 输出固定提示。CLI 默认数据目录通过 `Environment.GetFolderPath(UserProfile) + ".mohist"` 内联解析。
- **BannedApiAnalyzer**：`TreatWarningsAsErrors` + 禁止直接调用 `System.Environment` / `System.IO` 静态方法，必须注入 `IEnvironmentVariableProvider` / `IFileSystem`。
- **测试夹具**：`MohistDbFixture` 复用 `ConfigureMohistServices(config)`，新服务注册会自动被测试覆盖。

相关 spec：
- `otel-trace-collection`：OTLP 端口绑定、JSON ingestion、Protobuf 415、`otel.db` 持久化与 schema 契约
- `otel-trace-query-api`：主端口上的 trace 列表、SQL 查询、status 端点
- `otel-cli`：`mo otel query`（直读 SQLite）与 `mo otel status`（HTTP 探测）
- `server-daemon` (delta)：双端口启动
- `http-api` (delta)：`/otel/api/` 路由组
- `cli-interface` (delta)：`mo otel` 命令组

## Goals / Non-Goals

**Goals:**

- Server 启动时额外绑定 OTLP ingestion 端口（默认 4318），接收标准 OTLP HTTP/JSON trace
- Trace 持久化到独立 `otel.db`，与主业务库文件级隔离
- 主 API 端口暴露 trace 列表查询、自由 SQL 查询、collector 状态三个端点
- CLI 提供 `mo otel query`（直读 SQLite、不依赖 server）和 `mo otel status`（HTTP 探测）
- 外部应用通过标准 OTel SDK 环境变量零改造接入
- OTLP 端口失败不阻断主 API

**Non-Goals（第一期）：**

- 不收 log / metric（只收 trace）
- 不做 Web UI 可视化（后续 issue）
- 不做 gRPC 传输、Protobuf 编码
- 不做数据保留 / 自动清理策略
- 不做 TLS / 认证
- 不做 trace 采样、过滤
- 不引入 OpenTelemetry SDK 用于 server 自身仪表化（这是 receiver，不是 instrumented app）

## Decisions

### Decision 1: 双端口实现——Kestrel 双 URL + `RequireHost` 路由隔离

**选择**：用 `builder.WebHost.UseUrls("http://<host>:<mainPort>;http://<host>:<otelPort>")` 同时绑定两个端口，在 OTLP 路由组上用 `.RequireHost("<host>:<otelPort>")` 限制只在 OTLP 端口可见；再加一道轻量 middleware 拦截 OTLP 端口上的非 `/otel/v1/` 路径（返回 404），保证 OTLP 端口不泄漏主 API 路由。

**备选**：
- *Kestrel named endpoints*（`Configure<KestrelServerOptions>` + `Listen` + endpoint name）—— 更精细但引入全新配置模式，代码库无先例。如果未来需要 per-endpoint TLS/HTTP2 设置，可迁移到这个方案。
- *两个 `WebApplication` 实例共享 DI* —— 隔离最强但 ASP.NET Core 不官方支持共享 service provider 跑两个 `WebApplication`，实现复杂。

**理由**：`UseUrls` 双 URL 是对 `Program.cs` 现有 `UseUrls` 逻辑的最小自然扩展。`RequireHost` 匹配 HTTP `Host` 头（含端口），能可靠区分请求到达哪个端口。全局 middleware 的开销是一次 `Host` 头字符串检查，可忽略。OTLP 端口绑定失败时只 catch 这一段，主端口不受影响——满足 spec 的"失败不阻断"要求。

**实现要点**：
- `Program.cs` 中把现有 `UseUrls(...)` 扩展为拼接两个 URL（主端口逻辑不变，追加 OTLP 端口）
- OTLP 端口 URL 从 `Mohist:Otel:Port` 读取，默认 4318；读取/绑定放在 try/catch 里，失败记录日志但不抛
- 新增 `OtelPortIsolationMiddleware`：检查请求 `Host` 头的端口部分，若属于 OTLP 端口且路径不以 `/otel/v1/` 开头，返回 404
- collector 在线状态（OTLP 端口是否成功绑定）存入一个单例 `OtelCollectorStatus`，供 `/otel/api/status` 读取

### Decision 2: OTLP JSON 解析——手写 POCO + `System.Text.Json`，不引入 OTel 包

**选择**：为 OTLP JSON trace schema 手写一组 POCO（`ResourceSpans`、`ScopeSpans`、`Span`、`AnyValue` 等），用 `System.Text.Json` + camelCase `JsonSerializerOptions` 反序列化。不引入 `OpenTelemetry.Protobuf` / `Google.Protobuf` 任何新包。

**备选**：
- *`OpenTelemetry.Protobuf`（生成类型）* —— 官方 proto 类型，但拉入 `Google.Protobuf` 依赖；这些类型面向序列化而非接收，JSON 映射需要额外适配层；体积大。
- *`System.Text.Json` + `JsonDocument` DOM* —— 不需要定义 POCO，但遍历 DOM 写入 DB 的代码更冗长、类型不安全。

**理由**：OTLP HTTP/JSON 编码是稳定的、schema 简单（主要是嵌套数组 + `oneof` value），手写 POCO 约 5-7 个类即可覆盖。零新依赖。`oneof` value（`stringValue` / `intValue` / `doubleValue` / `boolValue` / `arrayValue` / `kvlistValue`）用一个自定义 `JsonConverter` 处理即可。这也避免了 `Google.Protobuf` 与 Orleans 序列化的潜在冲突。

### Decision 3: `otel.db` 数据访问——原生 `Microsoft.Data.Sqlite`，不用 EF Core

**选择**：为 `otel.db` 使用原生 `Microsoft.Data.Sqlite`（`SqliteConnection` + `SqliteCommand`），不建 `DbContext`、不用 EF Core 迁移。schema 用启动时执行的原始 DDL 初始化（`CREATE TABLE IF NOT EXISTS ...`）。

**备选**：
- *第二个 `DbContext`（`OtelDbContext`）* —— 与主库模式一致，但 trace 是高频率简单 INSERT，EF Core 的变更追踪/迁移机制是多余的负担；且自由 SQL 查询端点本来就要落到原生 `SqliteCommand`，混用两层 ORM 反而割裂。
- *Dapper* —— 轻量 ORM，但代码库无 Dapper 先例，为一个微场景引入不值得。

**理由**：`Microsoft.Data.Sqlite` 已是 server 的间接依赖（EF Core SQLite provider 底层就是它），直接使用不增加包。schema 固定（2 表 + 索引）、查询模式简单（INSERT 批量、SELECT 列表/自由 SQL），原生 ADO.NET 最直接。这也让 `POST /otel/api/query` 的"执行用户 SQL"逻辑天然落到同一层。开启 WAL 模式（`PRAGMA journal_mode=WAL`）以支持并发读写。

### Decision 4: Schema 细节与时间存储格式

DDL（由 spec 契约约束，此处补充实现细节）：

```sql
CREATE TABLE IF NOT EXISTS traces (
    trace_id    TEXT PRIMARY KEY,
    service_name TEXT NOT NULL,
    start_time  TEXT NOT NULL,   -- RFC3339/ISO 8601 UTC，如 2026-06-21T01:02:03.123456789Z
    end_time    TEXT NOT NULL,
    span_count  INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_traces_service_start ON traces(service_name, start_time DESC);
CREATE INDEX IF NOT EXISTS idx_traces_start ON traces(start_time DESC);

CREATE TABLE IF NOT EXISTS spans (
    trace_id           TEXT NOT NULL,
    span_id            TEXT NOT NULL,
    parent_span_id     TEXT,
    name               TEXT NOT NULL,
    kind               INTEGER NOT NULL,
    start_time         TEXT NOT NULL,
    end_time           TEXT NOT NULL,
    attributes         TEXT,       -- JSON 对象数组序列化
    status_code        INTEGER NOT NULL DEFAULT 0,
    status_message     TEXT,
    resource_attributes TEXT,      -- JSON 对象数组序列化
    PRIMARY KEY (trace_id, span_id)
);
CREATE INDEX IF NOT EXISTS idx_spans_trace ON spans(trace_id);
```

**关键选择**：

- **时间存储为 ISO 8601 文本**而非原始 nanosecond 整数。OTLP 传来的是 `startTimeUnixNano` / `endTimeUnixNano`（字符串形式的纳秒时间戳），写入时转换为 ISO 8601 UTC 字符串。理由：(a) CLI 直读时人类可读；(b) SQLite TEXT 的字典序与时间序一致，`ORDER BY start_time DESC` 自然正确；(c) `julianday()` / `strftime()` 函数仍可解析。
- **`traces.trace_id` 单主键**——一个 trace 一行。OTLP 的 `resourceSpans` 可能含多个 resource（多服务），v1 取第一个出现的 `service.name` 作为 `service_name`。这是已知简化（见 Risks）。
- **`attributes` / `resource_attributes` 存 JSON 文本**——OTLP 的 `attributes` 是 `KeyValue[]`，每个 `{ key, value: AnyValue }`。写入时序列化为 JSON 字符串存入 TEXT 列。CLI / API 查询时由调用方自行 `json_extract()` 或解析。
- **幂等写入**：同一 trace 的 span 可能分多个 OTLP 请求到达。`INSERT OR REPLACE INTO spans` 保证幂等；`traces` 表的 `span_count` 在每次 span 写入时 upsert 更新（`INSERT OR REPLACE` + 子查询，或读后算）。

### Decision 5: SQL 查询安全——只读连接 + 关键字拒绝

`POST /otel/api/query` 和 `mo otel query` 的安全层级：

1. **只读连接**：SQLite 连接串附加 `Mode=ReadOnly`。这是主防线——即使关键字检查被绕过，物理上不可写。
2. **关键字拒绝**：执行前检查 SQL（去注释、规范化空白后）是否以 `SELECT` 开头（大小写不敏感），并拒绝 `INSERT`/`UPDATE`/`DELETE`/`DROP`/`ALTER`/`ATTACH`/`DETACH`/`PRAGMA`/`VACUUM`/`REINDEX` 关键字作为顶层语句。返回 HTTP 400。
3. **超时**：查询执行加 5 秒 `CommandTimeout`，防止恶意大查询占住连接。

CLI 端 `mo otel query` 不做关键字拒绝（用户在自己的数据库上执行自己的 SQL，风险自担），但仍用只读连接避免误写。

### Decision 6: 配置——`Mohist:Otel` 段，遵循现有 Options 模式

```csharp
public sealed class OtelOptions
{
    public const string SectionName = "Mohist:Otel";
    public int Port { get; set; } = 4318;
    public string? DbPath { get; set; }   // 默认 ~/.mohist/otel.db
    public bool Enabled { get; set; } = true;
}
```

- 绑定：`services.Configure<OtelOptions>(configuration.GetSection(OtelOptions.SectionName))`
- DbPath 解析复用 `ResolveSqliteConnectionString` 的同款逻辑（home 目录 + `.mohist`），但文件名是 `otel.db`；支持 `Mohist:Otel:DbPath` 覆盖
- `Enabled = false` 时完全跳过 OTLP 端口绑定与 `/otel/api/*` 注册（留作运维开关）

### Decision 7: 代码组织

Server（`packages/server/src/Mohist.Server/`）：

```
Otel/
  OtelOptions.cs              -- 配置
  OtelCollectorStatus.cs      -- 单例，记录 OTLP 端口绑定状态
  OtelDb.cs                   -- 连接串解析 + schema 初始化（DDL）+ 连接工厂
  OtlpJson/                   -- OTLP JSON POCO + JsonConverter
    OtlpTraceRequest.cs       -- 顶层 resourceSpans
    OtlpModels.cs             -- Resource/Scope/Span/Status/AnyValue
    AnyValueConverter.cs      -- oneof value 自定义转换器
  TraceIngester.cs            -- 解析 OTLP JSON → 写入 otel.db（batch INSERT）
  TraceQuerier.cs             -- traces 列表查询 + status 统计
Api/
  OtlpRoutes.cs               -- OTLP 端口：POST /otel/v1/traces（返回原始 OTLP JSON）
  OtelQueryRoutes.cs          -- 主端口：GET /otel/api/traces, POST /otel/api/query, GET /otel/api/status
Infrastructure/Hosting/
  MohistApiRegistration.cs    -- 追加 MapOtlpRoutes() / MapOtelQueryRoutes()
  MohistServiceRegistration.cs -- 追加 OtelOptions 绑定 + OtelDb + TraceIngester + TraceQuerier 注册
```

CLI（`packages/cli/Mohist.Cli/`）：

```
MohistCliCommands.Otel.cs     -- mo otel query / mo otel status 命令组
OtelDb.cs 或复用 server 的 OtelDb.cs（如果共享项目结构允许）
```

CLI 需要新增 `Microsoft.Data.Sqlite` 包引用（目前只在 server 的 .csproj 间接依赖）。

### Decision 8: OTLP 端点不套用 `ApiResponse<T>` 信封

`POST /otel/v1/traces` 必须返回标准 OTLP 响应 `{}`（空 JSON 对象），不能是 `{ success: true, data: {} }`。因此 `OtlpRoutes.cs` 直接用 `Results.Ok("{}")` 或 `Results.Json(new {})`，绕过 `ApiResults`。错误也用原始 HTTP 状态码 + OTLP 兼容的 JSON 错误体。

主端口上的 `/otel/api/*` 端点**是否**套用 `ApiResponse<T>` 信封？为了与 `/api/*` 体系一致（CLI 与 AI agent 已习惯这个信封），`GET /otel/api/traces` 与 `GET /otel/api/status` 套用信封；`POST /otel/api/query` 因返回的是任意结果集，`data` 字段直接放数组。Spec 里写的是"返回 JSON 数组"——如果套信封，实际是 `{ success: true, data: [...] }`。这是 spec 的实现细节澄清，不算偏差：调用方（CLI、AI agent）统一用信封解析器。

## Risks / Trade-offs

- **[OTLP 端口 4318 被占用]** → 多数本地装过 Jaeger/OTel collector 的开发机会冲突。缓解：`Mohist:Otel:Port` 可配；绑定失败只记日志、主 API 不受影响；`/otel/api/status` 如实报告 `collector_online: false`。
- **[`traces` 单 trace 多服务简化]** → 一个跨 service 的 trace（如 Runner → Server）在 OTLP 里是多个 `resourceSpans`，v1 只记第一个 `service.name`。`?service=` 过滤可能漏掉次级 service 的 span（span 仍在 `spans` 表，但 `traces` 行的 `service_name` 只有一个）。缓解：文档说明限制；未来若需要可改 `(trace_id, service_name)` 联合主键，属 breaking 变更需走 spec 演进。
- **[`otel.db` 无上限增长]** → 第一期不做保留策略，长期运行会膨胀。缓解：用户可用 `mo otel query` 自查大小后手动删文件（server 重启重建）；保留策略列为后续 issue。文档里明确。
- **[SQL 注入面]** → `POST /otel/api/query` 暴露任意 SELECT。缓解：只读连接（物理隔离）+ 关键字拒绝 + 5s 超时；端点只在 localhost 主端口暴露。
- **[SQLite 写入并发]** → 高频 trace 下单写入连接可能成为瓶颈。缓解：WAL 模式；ingester 用单写队列串行化写入（避免锁竞争），批量提交。第一期本地单机场景吞吐够用。
- **[OTLP schema 演进]** → 手写 POCO 不自动跟随 OTel proto 升级。缓解：OTLP HTTP/JSON 1.x 稳定；忽略未知字段（`JsonSerializerOptions { DefaultIgnoreCondition = WhenWritingNull }`）；新字段不破坏 v1 写入路径。
- **[双端口 `UseUrls` 与 `ASPNETCORE_URLS` 覆盖交互]** → 现有逻辑是"若 `urls`/`ASPNETCORE_URLS` 已设则不调 `UseUrls`"。OTLP 端口需要在所有情况下都能追加。缓解：OTLP 端口绑定独立于主 URL 解析（用 Kestrel `Listen` 或在 `UseUrls` 之外单独配置），不与主端口 override 逻辑耦合。

## Migration Plan

**前置条件**：无。纯新增，不触碰现有数据库、路由、配置键。

**部署步骤**：

1. 合并代码后重新构建 server（`npm run build`）
2. 停止运行中的 server（`mo server stop`）
3. 重新启动（`mo server start`）——新版本自动：
   - 绑定 OTLP 端口 4318（或配置的端口）
   - 创建 `~/.mohist/otel.db` 并初始化 schema
   - 注册 `/otel/api/*` 路由
4. （可选）在 `~/.mohist/config.jsonc` 加 `"Mohist": { "Otel": { "Port": 14318 } }` 自定义端口
5. （可选）外部应用配置 `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318/otel` + `OTEL_EXPORTER_OTLP_PROTOCOL=http/json` 开始发 trace

**回滚策略**：

1. 停 server
2. 部署旧版本二进制
3. （可选）删除 `~/.mohist/otel.db`（不会影响主业务数据）
4. 旧版本不识别 `Mohist:Otel` 配置段——多余的配置键被忽略，无副作用

**无需数据迁移**：`otel.db` 是新文件，主库 `mohist.db` 无任何变更。

## Open Questions

1. **`/otel/api/*` 是否套用 `ApiResponse<T>` 信封？** 设计当前选择"是"（与主 API 一致）。若希望 AI agent 直接拿到裸数组便于解析，需调整 spec 的响应描述。倾向保留信封，由调用方解包。
2. **CLI 是否共享 server 的 `OtelDb.cs` / OTLP POCO？** 若抽成 `Mohist.Shared` 项目可复用，但当前仓库无 shared 项目惯例。倾向 CLI 端最小复制（只复用 schema DDL 常量），避免为小代码建共享项目。
3. **OTLP 端口是否监听 `localhost` 之外（`0.0.0.0`）？** 默认 `localhost` 与主端口一致。若需远程发 trace 要改成 `0.0.0.0`——但这会把未认证的 ingest 暴露到网络。v1 固定 `localhost`，文档说明限制。
4. **`mo otel query` 输出格式**：表格 vs JSON。倾向默认表格（人读），加 `-o json`（机器读），复用现有 `OutputOption()` 约定。需在实现时确认 `MohistCliApi.TableShape` 能渲染任意列宽的 SQLite 结果集。
