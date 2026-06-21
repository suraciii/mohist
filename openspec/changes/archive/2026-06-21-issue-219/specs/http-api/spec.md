## ADDED Requirements

### Requirement: 主 API 端口暴露 OTel 查询路由组

主 API 端口 SHALL 在现有 `/api/` 路由之外注册 `/otel/api/` 路由组，供 CLI 与 AI agent 查询 trace 数据与 collector 状态。该路由组 SHALL 至少包含以下端点：`GET /otel/api/traces`（trace 列表查询）、`POST /otel/api/query`（自定义 SQL 查询）、`GET /otel/api/status`（collector 状态）。该路由组 SHALL 在主 API 端口上暴露（默认 3456），而非 OTLP ingestion 端口。

各端点的详细行为、请求/响应格式由 `otel-trace-query-api` capability 定义。

#### Scenario: /otel/api/ 路由组可从主端口访问
- **WHEN** 客户端从主 API 端口请求 `GET /otel/api/traces`
- **THEN** SHALL 返回 trace 列表（具体格式见 `otel-trace-query-api`）

#### Scenario: /otel/api/ 路由组不暴露在 OTLP ingestion 端口
- **WHEN** 客户端从 OTLP ingestion 端口（4318）请求 `GET /otel/api/traces`
- **THEN** SHALL 返回 HTTP 404
- **AND** OTLP ingestion 端口 SHALL 只暴露 OTLP 协议端点（`/otel/v1/traces`）
