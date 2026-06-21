## ADDED Requirements

### Requirement: GET /otel/api/traces 返回最近 trace 列表

主 API 端口 SHALL 暴露 `GET /otel/api/traces` 端点，返回最近的 trace 列表。该端点 SHALL 支持可选查询参数 `?limit=<N>`（限制返回条数，默认 50，最大 1000）和 `?service=<name>`（按 `service_name` 过滤）。响应 SHALL 为 JSON 数组，每个元素至少包含 `trace_id`、`service_name`、`start_time`、`end_time`、`span_count`。结果 SHALL 按 `start_time` 降序排列。

#### Scenario: 默认返回最近 trace
- **WHEN** 客户端请求 `GET /otel/api/traces`
- **THEN** SHALL 返回 HTTP 200
- **AND** 响应体 SHALL 为 JSON 数组，最多包含 50 条 trace
- **AND** 每条记录 SHALL 包含 `trace_id`、`service_name`、`start_time`、`end_time`、`span_count`
- **AND** 结果 SHALL 按 `start_time` 降序排列

#### Scenario: 使用 limit 参数
- **WHEN** 客户端请求 `GET /otel/api/traces?limit=5`
- **THEN** SHALL 返回最多 5 条 trace 记录

#### Scenario: 使用 service 过滤
- **WHEN** 客户端请求 `GET /otel/api/traces?service=runner`
- **THEN** SHALL 只返回 `service_name` 为 `runner` 的 trace 记录

#### Scenario: limit 与 service 组合
- **WHEN** 客户端请求 `GET /otel/api/traces?limit=10&service=server`
- **THEN** SHALL 返回最多 10 条 `service_name` 为 `server` 的 trace

#### Scenario: limit 超过上限被截断
- **WHEN** 客户端请求 `GET /otel/api/traces?limit=5000`
- **THEN** SHALL 最多返回 1000 条记录

#### Scenario: 无数据时返回空数组
- **WHEN** 客户端请求 `GET /otel/api/traces`
- **AND** `otel.db` 中没有任何 trace
- **THEN** SHALL 返回 HTTP 200 与空数组 `[]`

### Requirement: POST /otel/api/query 执行自定义 SQL 查询

主 API 端口 SHALL 暴露 `POST /otel/api/query` 端点，接受 JSON 请求体 `{"sql": "SELECT ..."}`，在 `otel.db` 上执行只读 SQL 查询，返回 JSON 格式结果。端点 SHALL 只允许 `SELECT` 语句，拒绝任何写操作（`INSERT`、`UPDATE`、`DELETE`、`DROP`、`ALTER`、`ATTACH`、`PRAGMA`）。查询 SHALL 以只读连接执行。

#### Scenario: 执行合法 SELECT 查询
- **WHEN** 客户端发送 `POST /otel/api/query` 且 body 为 `{"sql": "SELECT COUNT(*) FROM traces"}`
- **THEN** SHALL 返回 HTTP 200
- **AND** 响应体 SHALL 为 JSON 格式的查询结果（如 `[{"COUNT(*)": 42}]`）

#### Scenario: 拒绝写操作
- **WHEN** 客户端发送 `POST /otel/api/query` 且 body 为 `{"sql": "DELETE FROM traces"}`
- **THEN** SHALL 返回 HTTP 400
- **AND** 响应体 SHALL 说明只允许 SELECT 查询

#### Scenario: 缺少 sql 字段
- **WHEN** 客户端发送 `POST /otel/api/query` 且 body 为 `{}`
- **THEN** SHALL 返回 HTTP 400
- **AND** 响应体 SHALL 说明缺少 `sql` 字段

#### Scenario: SQL 语法错误
- **WHEN** 客户端发送 body `{"sql": "SELECT FROM traces"}`
- **THEN** SHALL 返回 HTTP 400
- **AND** 响应体 SHALL 包含底层 SQLite 的错误信息

#### Scenario: 查询不存在的表
- **WHEN** 客户端发送 body `{"sql": "SELECT * FROM nonexistent"}`
- **THEN** SHALL 返回 HTTP 400
- **AND** 响应体 SHALL 包含 "no such table" 错误信息

### Requirement: GET /otel/api/status 返回 collector 状态

主 API 端口 SHALL 暴露 `GET /otel/api/status` 端点，返回 OTel collector 的运行状态与数据库统计。响应 SHALL 包含：`collector_online`（boolean，OTLP ingestion 端口是否在监听）、`db_size_bytes`（`otel.db` 文件大小）、`trace_count`（`traces` 表总条数）、`span_count`（`spans` 表总条数）。

#### Scenario: collector 在线
- **WHEN** 客户端请求 `GET /otel/api/status`
- **AND** OTLP ingestion 端口正常监听
- **THEN** SHALL 返回 HTTP 200
- **AND** 响应体 `collector_online` SHALL 为 `true`
- **AND** 响应体 SHALL 包含 `db_size_bytes`、`trace_count`、`span_count`

#### Scenario: collector 离线
- **WHEN** 客户端请求 `GET /otel/api/status`
- **AND** OTLP ingestion 端口未启动（如端口绑定失败）
- **THEN** SHALL 返回 HTTP 200
- **AND** 响应体 `collector_online` SHALL 为 `false`
- **AND** 其他统计字段 SHALL 仍正常返回（若 `otel.db` 可访问）

#### Scenario: 数据库未初始化
- **WHEN** 客户端请求 `GET /otel/api/status`
- **AND** `otel.db` 不存在
- **THEN** SHALL 返回 HTTP 200
- **AND** `collector_online` 反映实际端口状态
- **AND** `db_size_bytes` SHALL 为 `0`
- **AND** `trace_count`、`span_count` SHALL 为 `0`
