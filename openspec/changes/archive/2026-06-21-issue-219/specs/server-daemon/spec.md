## ADDED Requirements

### Requirement: Server 启动时绑定 OTLP ingestion 端口

Server 启动时 SHALL 在主 API 端口之外额外绑定一个 OTLP HTTP ingestion 端口（默认 `4318`，可通过配置项 `Mohist:Otel:Port` 覆盖）。主进程 SHALL 同时持有两个监听端口：主 API 端口（默认 3456）与 OTLP ingestion 端口。两个端口的 OTel 相关路径 SHALL 统一使用 `/otel/` 前缀。OTLP 端口 SHALL 只处理 OTLP 协议端点，不代理主 API 端口的路由。OTLP 端口绑定失败 SHALL NOT 阻止主 API 端口启动。

详细的 OTLP 端点行为（请求格式、编码校验、持久化）由 `otel-trace-collection` capability 定义。

#### Scenario: 双端口同时启动
- **WHEN** 用户执行 `mo server start`
- **AND** 未配置自定义端口
- **THEN** server SHALL 在 `localhost:3456` 监听主 API 请求
- **AND** server SHALL 在 `localhost:4318` 监听 OTLP HTTP 请求

#### Scenario: 自定义 OTLP 端口
- **WHEN** 配置 `Mohist:Otel:Port` 为 `14318`
- **THEN** server SHALL 在 `localhost:14318` 监听 OTLP HTTP 请求
- **AND** 主 API 端口 SHALL 不受影响

#### Scenario: server 停止时双端口同时释放
- **WHEN** 用户执行 `mo server stop`
- **THEN** 主 API 端口与 OTLP ingestion 端口 SHALL 同时停止监听
