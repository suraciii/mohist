## Why

Runner 的 ACP（Agent Client Protocol）适配层把一整个未分解的子系统——会话生命周期、存活探活、上下文压缩、模型解析、事件归一化、进程生成——塞进了单一文件 `packages/runner/src/actions/acp-agent.ts`（1552 行）与同等规模的测试（`acp-agent.spec.ts` 1775 行）。它是 runner 与 agent 通信的核心适配层，也是被最频繁改动的部分之一，但每次增改都要求在一个巨型文件里小心地穿行几百个分支，改动风险不断累积，且不敢重构。这违背了"按变更原因切分"的模块原则：压缩、探活、模型解析彼此独立地变更，却被强耦合在同一文件里。

## What Changes

- 把 `actions/acp-agent.ts` 沿职责 seam 拆为一组聚焦模块（`actions/acp/` 子目录），每个模块只承担一个变更原因：`process.ts`（进程生成/测试工厂）、`session-strategies.ts`（4 种会话生命周期）、`liveness.ts`（存活状态机/探活/监控循环）、`compaction.ts`（压缩配置与事件）、`model-resolution.ts`（模型解析/复用/事件）、`session-events.ts`（更新归一化/可观测发射/落库）、`agent-config.ts`（配置解析/prompt loader）。
- `actions/acp-agent.ts` 降为瘦入口：保留 action 编排 + 跨簇小助手（超时/取消/错误格式化）+ 对 5 个公共导出的再导出。
- **对外公共面冻结不变**：外部消费者（`registry.ts`、`executor.ts`、`rebase.ts`）及测试所依赖的 5 个导出——`acpAgentAction`、`setAcpProcessFactoryForTest`、`AcpProcessHandle`、`defaultCompactionConfig`、`resolveCompactionConfig`——由瘦入口原样再导出，零改动。
- 测试 `acp-agent.spec.ts` 按其已有的 `describe` 块拆分为对应文件，每个文件聚焦一个簇。
- **不改任何可观察行为**：ACP 协议消息处理顺序、会话生命周期判定、存活探活语义、与 agent 的交互时序、与上游 runner runtime 的调用契约完全保持不变。

## Capabilities

### New Capabilities

无。本次改动是 runner 内部的纯重构——按变更原因重组模块边界，不引入任何新的可观察系统行为。

### Modified Capabilities

无。spec 级行为要求不变，仅实现/模块组织变化：

- `agent-runtime`：ACP 适配层的运行时行为（事件捕获、模型解析、会话时序）要求不变，只搬实现，故不产生 delta spec。

## Impact

- **`packages/runner/src/actions/acp-agent.ts`**：由 1552 行子系统降为瘦入口（action 编排 + 跨簇助手 + 公共面再导出），其余 ~130+ 内部符号迁入 `actions/acp/` 下 7 个聚焦模块。
- **新增 `packages/runner/src/actions/acp/`**：`process.ts`、`session-strategies.ts`、`liveness.ts`、`compaction.ts`、`model-resolution.ts`、`session-events.ts`、`agent-config.ts`。
- **`packages/runner/tests/acp-agent.spec.ts`**（1775 行）：按现有 `describe` 块拆为多个聚焦测试文件。
- **外部消费者零改动**：`runtime/executor.ts`、`actions/registry.ts`、`actions/rebase.ts` 的 import 路径不变；`tests/support/fake-acp.ts`、`tests/acp-tool-noise.spec.ts` 的 import 不变。
- **无 API、协议、依赖、数据库、配置变更**；推进按"无状态窄依赖簇优先"递增，每步后跑 `npm run test:run -w packages/runner` 守护协议/时序零回归。
