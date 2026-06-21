# OpenSpec Capability: otel-trace-collection

### Requirement: Server 绑定 OTLP trace ingestion 端口

Server 启动时 SHALL 额外绑定一个 OTLP HTTP ingestion 端口，默认 `4318`，可通过配置项 `Mohist:Otel:Port` 覆盖。该端口 SHALL 只暴露 `/otel/` 前缀的 OTLP 端点，不暴露主 API 端口的其他路由。端口绑定失败（如端口被占用）SHALL 记录明确错误日志，但 SHALL NOT 导致主 API 端口启动失败；主 API 端口 SHALL 独立可用。

#### Scenario: 默认端口启动
- **WHEN** Server 启动且未配置 `Mohist:Otel:Port`
- **THEN** Server SHALL 在 `localhost:4318` 监听 OTLP HTTP 请求
- **AND** 主 API 端口（默认 3456）同时正常监听

#### Scenario: 自定义端口
- **WHEN** 配置 `Mohist:Otel:Port` 为 `14318`
- **THEN** Server SHALL 在 `localhost:14318` 监听 OTLP HTTP 请求

#### Scenario: OTLP 端口被占用不阻断主 API
- **WHEN** 配置的 OTLP 端口已被其他进程占用
- **THEN** Server SHALL 记录错误日志说明 OTLP 端口绑定失败
- **AND** 主 API 端口 SHALL 继续正常启动并服务请求
- **AND** `/otel/api/status` 端点 SHALL 报告 collector 状态为不可用

### Requirement: POST /otel/v1/traces 接收 OTLP HTTP JSON trace

Server SHALL 在 OTLP ingestion 端口上暴露 `POST /otel/v1/traces` 端点，接受符合 OpenTelemetry Protocol HTTP JSON 编码规范的 trace 请求体（`Content-Type: application/json`）。成功时 SHALL 返回 HTTP 200 与标准 OTLP JSON 响应体 `{}`。Server SHALL 解析请求体中的 `resourceSpans` 结构并提取所有 span 数据。

#### Scenario: 接受合法 OTLP JSON trace
- **WHEN** 客户端发送 `POST /otel/v1/traces`
- **AND** `Content-Type: application/json`
- **AND** 请求体为合法 OTLP JSON（包含 `resourceSpans`）
- **THEN** Server SHALL 返回 HTTP 200
- **AND** 响应体 SHALL 为 `{}`
- **AND** Server SHALL 将所有 span 持久化到 `otel.db`

#### Scenario: 标准 OTel SDK 发送的 trace 正确写入
- **WHEN** 外部应用配置 `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4318/otel` 与 `OTEL_EXPORTER_OTLP_PROTOCOL=http/json`
- **AND** 应用通过标准 OpenTelemetry SDK 发送 trace
- **THEN** trace 数据 SHALL 正确写入 `otel.db`
- **AND** 可通过 `GET /otel/api/traces` 或 `mo otel query` 查询到

#### Scenario: 请求体 JSON 格式错误
- **WHEN** 客户端发送 `POST /otel/v1/traces` 且 `Content-Type: application/json`
- **AND** 请求体不是合法 JSON
- **THEN** Server SHALL 返回 HTTP 400
- **AND** 响应体 SHALL 包含错误说明

### Requirement: 拒绝 Protobuf 编码

`POST /otel/v1/traces` 端点 SHALL 只支持 OTLP HTTP JSON 编码。对 `Content-Type: application/x-protobuf` 的请求 SHALL 返回 `415 Unsupported Media Type`。第一期不支持 Protobuf 编码。

#### Scenario: 拒绝 Protobuf 请求
- **WHEN** 客户端发送 `POST /otel/v1/traces`
- **AND** `Content-Type: application/x-protobuf`
- **THEN** Server SHALL 返回 HTTP 415 Unsupported Media Type

#### Scenario: 不支持的 Content-Type
- **WHEN** 客户端发送 `POST /otel/v1/traces`
- **AND** `Content-Type` 为非 `application/json` 的任意值
- **THEN** Server SHALL 返回 HTTP 415 Unsupported Media Type

### Requirement: Trace 数据持久化到独立 otel.db

Server SHALL 将所有接收的 trace 数据持久化到独立 SQLite 文件 `otel.db`，该文件 SHALL 与主业务数据库位于同一数据目录（`~/.mohist/`），但 SHALL 是完全独立的文件，不与主业务库共享 schema 或连接。`otel.db` 路径 SHALL 可通过配置项 `Mohist:Otel:DbPath` 覆盖。Server 启动时若 `otel.db` 不存在 SHALL 自动创建并初始化 schema。

#### Scenario: 首次启动创建数据库
- **WHEN** Server 启动且 `~/.mohist/otel.db` 不存在
- **THEN** Server SHALL 创建 `otel.db` 文件
- **AND** SHALL 初始化 trace 存储所需的表结构

#### Scenario: 数据隔离
- **WHEN** trace 数据写入 `otel.db`
- **THEN** 主业务数据库文件 SHALL 不包含任何 trace 表或 trace 数据
- **AND** `otel.db` SHALL 不包含任何主业务表

#### Scenario: 自定义数据库路径
- **WHEN** 配置 `Mohist:Otel:DbPath` 为自定义路径
- **THEN** Server SHALL 使用该路径作为 `otel.db` 位置

### Requirement: otel.db 提供 trace 查询 schema 契约

`otel.db` SHALL 暴露稳定的表结构供 CLI 直接 SQL 查询。Schema SHALL 至少包含以下表与列：`traces` 表（`trace_id` TEXT 主键、`service_name` TEXT、`start_time` TIMESTAMP、`end_time` TIMESTAMP、`span_count` INTEGER）；`spans` 表（`trace_id` TEXT、`span_id` TEXT、`parent_span_id` TEXT NULL、`name` TEXT、`kind` INTEGER、`start_time` TIMESTAMP、`end_time` TIMESTAMP、`attributes` TEXT（JSON）、`status_code` INTEGER、`status_message` TEXT、`resource_attributes` TEXT（JSON)）。`spans.trace_id` SHALL 关联 `traces.trace_id`。此 schema 是 Server 写入与 CLI 直读共享的契约，变更需视为 breaking。

#### Scenario: traces 表可按 service 和时间查询
- **WHEN** 查询 `SELECT * FROM traces WHERE service_name = 'runner' ORDER BY start_time DESC LIMIT 10`
- **THEN** SHALL 返回最近属于 runner 服务的最多 10 条 trace 记录

#### Scenario: spans 表可按 trace_id 关联查询
- **WHEN** 查询 `SELECT * FROM spans WHERE trace_id = ?`
- **THEN** SHALL 返回该 trace 下的所有 span 记录

#### Scenario: attributes 以 JSON 文本存储
- **WHEN** 查询 span 的 `attributes` 或 `resource_attributes` 列
- **THEN** SHALL 返回 JSON 格式文本，可被调用方解析为键值对
