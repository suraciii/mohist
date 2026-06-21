## Why

AI agent 做程序化调试需要可查询的本地 trace 数据，但现有外部工具要么太重（Jaeger all-in-one ~500MB）、要么不可靠（otelite）、要么没有编程查询接口（otel-tui）。Mohist Server 目前不收集任何 trace，开发者若想观测 Runner / Server 自身执行轨迹无处可查。把轻量 OTel trace 收集器作为 Server 内置组件，可以零外部依赖地补齐"为 AI agent 服务的本地可查询 trace"这一空缺。

## What Changes

- 在 Mohist Server 内新增 OpenTelemetry trace 收集组件，作为 `/otel/` API group，启动即生效，无需独立部署
- **双端口架构**：在现有主 API 端口之外，额外监听 OTLP ingestion 端口（默认 4318，可通过 `Mohist:Otel:Port` 配置）；两个端口的路径统一使用 `/otel/` 前缀
- OTLP ingestion 端点 `POST /otel/v1/traces` 只接受 OTLP HTTP **JSON** 编码（`Content-Type: application/json`），对 `application/x-protobuf` 返回 `415 Unsupported Media Type`
- Trace 数据持久化到独立 SQLite 文件 `otel.db`（与主业务库同目录但完全隔离）
- 主 API 端口暴露查询接口：`GET /otel/api/traces`（支持 `?limit=`、`?service=` 过滤）和 `POST /otel/api/query`（接受 `{"sql": "SELECT ..."}` body，返回 JSON 结果）
- 新增 `mo otel` CLI 命令组：
  - `mo otel query "SELECT ..."` 直接读 SQLite（不走 server，server 关闭也能查历史；默认数据目录，支持 `-d` 指定路径）
  - `mo otel status` 通过 HTTP 探测主 server 的 OTel 状态端点判断 collector 是否在线，同时显示数据库大小和 trace 条数
- 外部应用（如 Runner）配置 `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318/otel` 与 `OTEL_EXPORTER_OTLP_PROTOCOL=http/json` 即可发送 trace
- **BREAKING**：无（纯新增能力，不修改已有接口语义）

## Capabilities

### New Capabilities

- `otel-trace-collection`: Server 内置 OTLP HTTP JSON trace 收集能力，包括额外端口绑定（`Mohist:Otel:Port`）、`POST /otel/v1/traces` 端点、JSON-only 编码校验（Protobuf 返回 415）、以及将 trace 持久化到独立 `otel.db` 的存储契约（schema 由本 capability 定义，供 CLI 直接读取）
- `otel-trace-query-api`: 主 API 端口上的 trace 查询接口，包括 `GET /otel/api/traces`（最近 trace 列表，支持 `?limit=`、`?service=` 过滤）和 `POST /otel/api/query`（`{"sql": "SELECT ..."}` 自定义 SQL 查询返回 JSON 结果），以及供 `mo otel status` 探测的 OTel 状态端点
- `otel-cli`: `mo otel` 命令组；`query` 子命令绕过 server 直接读 SQLite（默认数据目录，支持 `-d`），`status` 子命令通过 HTTP 探测 server 在线状态并附加数据库统计

### Modified Capabilities

- `server-daemon`: Server 启动时额外绑定 OTLP ingestion 端口（默认 4318，可配置 `Mohist:Otel:Port`），主进程同时持有两个监听端口
- `http-api`: 主 API 端口新增 `/otel/api/` 路由组（traces 列表、SQL 查询、collector 状态）
- `cli-interface`: 新增 `mo otel` 命令组及其 `query`、`status` 子命令

## Impact

- **Server (packages/server)**: 新增 OTel collector 组件（端口绑定、OTLP JSON 解析、SQLite 写入）；主 API 注册 `/otel/api/*` 路由；启动流程新增第二个端口监听；配置项 `Mohist:Otel:Port`
- **CLI (packages/cli)**: 新增 `mo otel` 命令组（`query` 直接 SQLite 访问、`status` HTTP 探测 + DB 统计）
- **数据目录**: 新增 `otel.db` SQLite 文件，与主业务库同目录、独立文件；定义 trace 存储schema（供 server 写入与 CLI 直读共享）
- **配置**: 新增 `Mohist:Otel:Port`（默认 4318）
- **SDK 对接**: Runner / 外部应用通过标准 OTel SDK 环境变量（`OTEL_EXPORTER_OTLP_ENDPOINT`、`OTEL_EXPORTER_OTLP_PROTOCOL`）接入，无需 Mohist 自定义客户端
- **依赖**: Server 需引入 OTLP JSON 解析能力（是否使用 OpenTelemetry SDK 取决于实现，design 阶段决定）
- **运行时边界**: collector 在 Control Plane 进程内运行；不执行副作用、不参与 workflow 决策；仅提供 trace ingest 与查询数据面
- **Non-Goals**（第一期）：不收 log/metric、不做 Web UI 可视化、不做采样/过滤、不做 gRPC 传输、不做 Protobuf 编码、不做数据保留/清理策略、不做多端口 TLS/认证
