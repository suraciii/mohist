## Context

内置观测的接收、写入分块、保留、存储预算、过载保护和运行状态已经就绪，但默认值仍将它关闭。`Mohist:Otel:Enabled` 同时影响出站 trace 管线、内置收集器和宿主的 OTLP 监听计划；目前这三个位置各自读取或绑定该键，且两个不同职责的 `OtelOptions` 类型都以 `false` 为默认值。Docker Compose 还显式覆盖为关闭。

本变更实现 `built-in-observability-defaults`：在没有配置时安全地启用内置观测，用户仍可通过同一开关完整退出。观测是辅助能力，不能成为 Issue、Workflow 或 Runner 的状态权威或可用性依赖。

## Goals / Non-Goals

**Goals:**

- 让未设置 `Mohist:Otel:Enabled` 的 Server 启动本地 OTLP 接收、Server trace 导出、诊断采样及存储维护。
- 让显式 `false` 在所有启动路径一致地关闭上述工作，并让状态保持 `off`。
- 保持默认 OTLP 接收在 `localhost:4318`，Compose 不发布该端口。
- 以启动组合测试和运行状态测试锁定默认与显式关闭行为。

**Non-Goals:**

- 不改变 OTLP 接收、保留、写入、存储预算或降级策略。
- 不新增配置键、运行时开关、认证或对外 OTLP 服务。
- 不改变 `mo otel status` 的协议或将观测数据变为业务数据。

## Decisions

### Use one default across all existing enablement readers

将 `Mohist.Server.Infrastructure.Config.OtelOptions.Enabled` 和 `Mohist.Server.Otel.OtelOptions.Enabled` 的属性默认值改为 `true`。`MohistHostFactory.CreatePrimaryPlan` 不再用缺失时为 `false` 的裸 `GetValue<bool>` 读取，而是通过收集器 options 的绑定结果取得 `Enabled`，使 OTLP listener plan 与收集器、诊断采样使用相同的缺省语义。显式配置值仍覆盖属性默认值。

两个 options 类型继续保留：前者是出站 SDK 的 endpoint 配置，后者是内置收集器的监听、存储与保留配置；它们共享开关但不共享其余职责。替代方案是合并为单一 options 类型，但会把两个基础设施组件的专有配置耦合在一次只需默认值对齐的改动中。另一替代方案是只修改 HostFactory 的缺省值；这会让出站 SDK 或后台维护仍可能关闭，产生部分启用状态。

### Remove the Compose opt-out instead of adding a Compose opt-in

从 `docker-compose.yml` 删除将 `Mohist:Otel:Enabled` 固定为 `false` 的环境变量和过期说明，令 Compose 继承 Server 的安全默认值。保留仅发布 `3456` 的 ports 列表；不添加 `4318` 映射。容器内 Server exporter 和 receiver 通过默认 loopback 端点通信，外部进程不能因此访问 receiver。

替代方案是在 Compose 中显式设为 `true`。这会掩盖产品默认值是否正确，并让非 Compose 安装仍然关闭。将 receiver 改为 `0.0.0.0` 并发布 `4318` 也被排除，因为 OTLP 接收没有认证，外部采集必须由操作者显式配置绑定与端口发布。

### Preserve the existing off-path and status surface

关闭路径继续通过现有条件注册和 hosted-service guard 跳过 trace provider、OTLP route/listener、诊断采样及维护循环；`/otel/api/status` 保持可查询并返回 `off`，使 `mo otel status` 能确认 opt-out。默认启用时，现有 collector bind、storage probe 和 protection 状态继续决定 `healthy` 或 `degraded`，不把短暂的初始化或保护状态伪装为健康。

替代方案是在关闭时移除状态路由。这样用户无法区分已关闭、服务不可达和路由缺失，且破坏现有 CLI 状态查询。

### Verify composition rather than duplicate collector behavior tests

为两种 options 默认值、HostFactory listener plan、OpenTelemetry registration、Otlp route registration和 diagnostics sampler 添加或调整聚焦测试。测试矩阵覆盖缺省启用、显式 `false`、默认 `localhost:4318` 和 Compose 未发布 `4318`。预算、保留和 OTLP 协议测试维持原有覆盖，不在本变更重复。

## Risks / Trade-offs

- [Existing local installations silently start collecting trace data after upgrade] -> 在配置和自托管文档说明新默认值、默认 72 小时和 1 GiB 预算，以及用 `Mohist:Otel:Enabled=false` 回退。
- [Three enablement readers drift again] -> 让 HostFactory 消费 options 绑定语义，并用缺省与显式关闭的组合测试同时断言 SDK、listener 和 runtime 状态。
- [OTLP listener bind failure makes default startup fail] -> 保留现有主机计划的 collector bind 失败分类与降级状态；确认观测降级不阻断主 API、Workflow 调度或 Runner 通信。
- [Operators mistake enabled observability for externally available OTLP ingest] -> 默认 loopback bind 且 Compose 不发布 `4318`；文档要求外部采集时显式配置网络暴露。

## Migration Plan

1. 更新两个 options 默认值与 HostFactory 的 enabled 解析，保持 `false` 的覆盖优先级。
2. 移除 Compose 的显式关闭配置，保留当前 API 端口映射。
3. 更新 `docs/observability.md` 和 `docs/self-host.md`，说明默认启用、默认本地接收、资源预算、状态查询与 opt-out；同步 `design/observability.md` 的“当前差距”，删除已完成的保留、存储上限、接收/写入边界和降级状态断言，只保留真实未实装的能力。
4. 运行 Server 相关 unit/spec 测试，重点验证缺省启动、显式关闭、listener 计划和 Compose 端口表面。
5. 发布后用 `mo otel status` 确认新实例为 `healthy` 或 `degraded`；出现资源或绑定问题时设置 `Mohist:Otel:Enabled=false` 并重启 Server，恢复完全关闭的既有行为。

## Open Questions

无。资源预算验收已经是默认启用的前置条件；对外 OTLP 接入继续由显式网络配置处理。
