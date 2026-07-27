## Why

内置观测已经具备接收、写入、保留和存储预算保护，以及可显示过载与降级状态的运行状态面，但仍默认关闭，导致长期运行的 Mohist 实例不能开箱获得诊断信号。资源预算验收通过后，应将这项受保护的辅助能力默认启用，同时保留用户主动关闭的选择。

## What Changes

- 将内置 OpenTelemetry 观测从默认关闭改为默认启用，使新建和默认部署的 Server 自动收集并查询受预算保护的观测数据。
- 保留 `Mohist:Otel:Enabled` 作为显式开关，用户关闭后不启动采集、导出或后台维护，并保持运行状态为 `off`。
- 更新默认部署配置与用户文档，说明默认监听范围、资源保护、状态查询和关闭方式。

## Capabilities

- `built-in-observability-defaults`: 内置观测在资源保护可用时的默认启用、显式关闭、默认部署行为和可见运行状态。

## Impact

- **Server**：`Mohist:Otel` 选项默认值、主机监听计划、OTLP 接收端口、运行时观测状态及其测试。
- **Deployment**：`docker-compose.yml` 的默认环境配置和端口暴露策略。
- **Documentation / CLI**：可观测性与自托管文档，以及 `mo otel status` 所呈现的默认运行状态。
