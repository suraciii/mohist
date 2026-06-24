## Context

`packages/runner/src/actions/acp-agent.ts`（1552 行）把整个 ACP 子系统塞进单一文件：进程生成、4 种会话生命周期（new/resume/reuse/ephemeral）、存活探活状态机与监控循环、上下文压缩配置与事件、模型解析与复用、更新归一化与可观测发射、agent 配置与 prompt loader。配套测试 `tests/acp-agent.spec.ts` 1775 行。它是 runner 与 agent 通信的核心适配层，也是最常被改动的部分之一，但每次改动都在一个巨型文件里穿行，风险持续累积。

现状约束（已核查）：
- **公共面只有 5 个导出被外部消费**：`acpAgentAction`（`executor.ts`/`registry.ts`/`rebase.ts`）、`setAcpProcessFactoryForTest` + `AcpProcessHandle`（测试）、`defaultCompactionConfig` + `resolveCompactionConfig`（测试断言）。另有支持性类型 `AcpProcessFactory`、`CompactionConfig`、`CompactionStrategy` 仅供上述导出签名使用。
- **测试天然分簇**：spec 有 5 个顶层 `describe`（main / shared session observability / compaction config helpers / cancelAndReturn bounded cleanup / monitorPrompt prompt_timeout diagnostics），即天然的切分线。
- **协议时序零容忍**：ACP 消息顺序、会话生命周期判定、探活语义、与 agent/runtime 的契约必须逐字节不变——由现有 spec 守护。

## Goals / Non-Goals

**Goals:**
- 把单文件子系统沿"变更原因"切为一组聚焦模块，每个模块只承担一个变更原因。
- 对外公共面（5 导出 + 支持类型）冻结不变，外部消费者与测试 import 路径零改动。
- 测试按簇拆分，单测试文件回到健康规模。
- 保持依赖图**严格无环**，模块间单向依赖。
- 全程被现有测试守护，每一步都可独立验证、可回退。

**Non-Goals:**
- 不改 ACP 协议、消息类型、会话状态、探活语义、生命周期判定。
- 不改 runner runtime 与适配层的调用契约。
- 不做性能优化、不引入新依赖。
- 不追求"行数均分"——按变更原因切，不按行数切。

## Decisions

### D1. 模块边界与符号归属（grounded in 实际调用图）

以 `createObservabilityAwareEmitter`（acp-agent.ts:265）为依赖汇聚点核查后，确定如下归属。凡"构造事件 payload + 发射到 server"的代码（都依赖 `cleanJson`/`sessionNameFromContext`/`emitSessionEvent`）集中到 `session-events.ts`，领域簇只提供**纯数据类型/抽取器**，从而保证无环。

| 模块 | 归属符号 | 对内依赖 |
|---|---|---|
| `acp/process.ts` | `AcpProcessHandle`/`AcpProcessFactory`/`setAcpProcessFactoryForTest`/`getAcpProcessFactory`/`createSpawnedAcpProcess`/`SpawnedAcpProcess` | core, system, acp-command（叶） |
| `acp/session-events.ts`（地基） | JSON 取值助手 `cleanJson`/`stringField`/`objectField`/`numberField`；`sessionNameFromContext`/`sessionTargetFromContext`；`emitSessionEvent`/`emitSessionStarted`/`emitResolvedModelEvent`/`attachSessionToServer`；所有 payload 构造器 `buildResolvedModelEventPayload`/`buildUsageUpdatePayload`/`hasUsageUpdateContent`/`buildCompactionEventPayload`/`buildLivenessEventPayload`/`buildPromptEvent`/`extractOutputPath`/`extractContextFiles`；归一化 `ToolCallIdGenerator`/`normalizeSessionUpdate`/`genericSessionEventType`/`inferToolName`/`createObservabilityAwareEmitter`/`createAcpSessionUpdateHandler`/`assistantMessageChunkText`/`hasMessageGrowth`；活动判定 `classifyAcpLivenessActivity`/`recordLivenessActivity`/`isPromptWorkActivity`/`QUALIFYING_LIVENESS_NOTIFICATION_TYPES`；事件名常量 | core, sdk（叶） |
| `acp/compaction.ts` | `CompactionConfig`/`CompactionStrategy`/`CompactionEventPayload`；默认值常量；`resolveCompactionConfig`/`defaultCompactionConfig`/`resolveCompactionConfigFromInput`/`buildSessionMeta`/`extractCompactionEventFromUpdate`（纯抽取，无 JSON 助手依赖） | core（叶） |
| `acp/model-resolution.ts` | `RequestedModel`/`resolveRequestedModel`/`applyRequestedModel`/`modelDiagnosticContext`/`requestedModelMatchesSession`/`cachedModelAllowsReuse`/`extractResolvedModelId`/`extractResolvedModelFromConfigUpdate`；折入 `errorMessage`（唯一消费者） | session-events |
| `acp/liveness.ts` | 存活状态机 `SessionLivenessState`/`LivenessProbeState`/`LivenessFailureReason`/`createSessionLivenessState`/`recordSessionLivenessActivity`/`beginLivenessProbe`/`clearLivenessProbe`/`hasPostProbeActivity`/`probeWasSatisfied`；`monitorPrompt`/`ensurePromptAcceptedOrPending`/`waitForData`；折入其唯一消费者的并发原语 `timeout`/`aborted`/`cancelAndReturn`/`toError` | session-events, opencode-log-diagnostics |
| `acp/agent-config.ts` | `AgentConfig`/`resolveAgentConfig`/`buildPromptLoaderContext` | compaction, core/json, core/prompt |
| `acp/session-strategies.ts` | 分发器 `runAcpWorkflowAgentSession`；4 运行器 `runNewWorkflowAgentSession`/`runResumedWorkflowAgentSession`/`runPromptOnExistingWorkflowAgentSession`/`runEphemeralWorkflowAgentSession`；`createSharedPromptRunner`/`validatePromptActivity`；期望修复循环 `satisfyExpectations`/`expectationRepairLimit`/`buildExpectationRepairPrompt`；文本累加 `appendAgentText`/`truncateAgentText`/`MAX_AGENT_TEXT_LENGTH`；结果类型 `AcpSessionResult`/`AcpPromptRunResult`/`AcpPromptRunner` | process, session-events, compaction, model-resolution, liveness(`timeout`), agent-config, expectations |
| `actions/acp-agent.ts`（瘦入口） | `acpAgentAction`；`restoreAgentToolNoise`；5 公共导出 + 支持类型再导出 | session-strategies, agent-config, expectations, session-events |

**对提案的两处务实细化**（依赖分析驱动，记录于此）：
- **期望修复循环归 `session-strategies` 而非入口。** `satisfyExpectations` 被 4 个运行器独占调用，且必须复用同一会话的 `runPrompt` 闭包做修复——无法上浮到入口。按"折入唯一消费者"原则归 `session-strategies.ts`，入口只保留 action 编排。否则入口↔session-strategies 成环。
- **并发原语（`timeout`/`aborted`/`cancelAndReturn`）归 `liveness` 而非入口。** `aborted`/`cancelAndReturn`/`toError` 仅 `monitorPrompt` 使用（折入）；`timeout` 被 liveness 与 session-strategies 共用——若放入口，则 session-strategies→入口↔入口→session-strategies 成环。放入 liveness（它本就是 session-strategies 的依赖），session-strategies 从 liveness 导入 `timeout`，图保持无环。

### D2. 无环依赖分层（严格单向）

```
process ──────────────────────────────────► (core/system)
compaction ───────────────────────────────► (core)
session-events ───────────────────────────► (core/sdk)
model-resolution ──► session-events
liveness ──────────► session-events
agent-config ──────► compaction
session-strategies ─► process, session-events, compaction, model-resolution, liveness, agent-config
acp-agent(入口) ───► session-strategies, agent-config, session-events, expectations
```

无任何反向边、无环。`session-events` 是地基（归一化+发射+JSON 助手+活动判定），其余领域簇是其单向消费者。

- **备选 A（被否）**：建一个 `acp/util.ts` 放 JSON 助手——违反"不建通用助手杂物袋"原则（会成为新的黑洞）。
- **备选 B（被否）**：保留 ES-module 运行期环（函数不在模块加载期互调，技术上可跑）——smelly，违背"职责单一、可独立演进"目标，且让单测隔离变难。

### D3. 公共面冻结策略

入口 `acp-agent.ts` 用 `export { ... } from "./acp/process.js"` / `./acp/compaction.js` 原样再导出 5 个公共符号及 3 个支持类型。外部消费者（`executor.ts`/`registry.ts`/`rebase.ts`/`tests/acp-agent.spec.ts`/`tests/acp-tool-noise.spec.ts`/`tests/support/fake-acp.ts`）的 import 路径与命名**完全不变**，故零改动。这是整个重构的安全锚——任何一步若让这 5 个导出签名或路径漂移，外部测试立即失败。

### D4. 测试按簇拆分（顺现有 describe 块）

| 新测试文件 | 来源 describe 块 |
|---|---|
| `tests/acp/session-strategies.spec.ts` | `"mohist/acp-agent"`（main，4 运行器） |
| `tests/acp/session-events.spec.ts` | `"shared session observability"` |
| `tests/acp/compaction.spec.ts` | `"compaction config helpers"` |
| `tests/acp/liveness.spec.ts` | `"cancelAndReturn bounded cleanup"` + `"monitorPrompt prompt_timeout diagnostics"` |

`acp-tool-noise.spec.ts`、`support/fake-acp.ts` 保持原位原路径（只消费公共面）。每个新测试文件聚焦一簇，单文件回到健康规模。

## Risks / Trade-offs

- **[协议时序回归] ->** 每步移动后跑 `npm run test:run -w packages/runner` 全量；liveness/monitorPrompt 步骤额外重点回归（探活时序最微妙）。重构期禁用任何"顺手改进"——纯搬运，符号行为零变化。
- **[公共面漂移] ->** 5 导出由入口再导出；任何外部消费者测试失败即定位漂移。重构前先确认基线测试全绿。
- **[依赖成环] ->** D2 的分层为硬约束；每抽一个模块用 `npm run typecheck -w packages/runner` 验证无环（环会触发 TS 循环导入告警或运行期 undefined）。
- **[session-events 过大（地基负担重）] ->** Trade-off：把所有 payload 构造+发射+JSON 助手集中于此换取无环；代价是 session-events 仍是较大模块。缓解：它职责单一（"ACP 更新 → 归一化 → 发射到 server"），变更原因单一，可接受。若后续膨胀可再沿"归一化 vs 发射"二次切分，但本次不做。
- **[并发原语归属争议] ->** `timeout` 放 liveness 而非通用处，是"无环"与"无杂物袋"两原则下的唯一解；记录于 D1，避免后续维护者误移。
- **[scc 未安装] ->** 验收项"单模块圈复杂度脱离前三"需在收尾时安装/借用 scc 复核；若无 scc，以"单文件行数显著下降 + 无单簇独大"作退守证据，并在 tasks 阶段补 scc 度量。

## Migration Plan

按依赖方向自底向上抽取，**每步独立可验证、可回退**（单 commit 一步）。注意：提案建议"compaction/model-resolution/process 先抽"，但实际调用图显示 model-resolution 依赖 session-events——故地基 `session-events` 必须早抽。细化顺序：

1. **`acp/process.ts`**（叶）——搬运进程相关 6 符号；入口改 import + 再导出 `AcpProcessHandle`/`AcpProcessFactory`/`setAcpProcessFactoryForTest`。→ 跑 typecheck + test。
2. **`acp/session-events.ts`**（地基）——搬运归一化/发射/JSON 助手/活动判定/所有 payload 构造器（D1 表）。→ typecheck + test（最大一步，重点看 observability 与各 emit 测试）。
3. **`acp/compaction.ts`**（叶，依赖已就位）——搬运压缩配置/默认值/meta/纯抽取器；入口再导出 `resolveCompactionConfig`/`defaultCompactionConfig`/`CompactionConfig`/`CompactionStrategy`。→ typecheck + test。
4. **`acp/model-resolution.ts`**——搬运模型解析/复用/抽取 + 折入 `errorMessage`。→ typecheck + test。
5. **`acp/liveness.ts`**——搬运存活状态机 + `monitorPrompt` + 并发原语（`timeout`/`aborted`/`cancelAndReturn`/`toError`）。→ typecheck + **重点回归**（探活/超时诊断）。
6. **`acp/agent-config.ts`**——搬运配置解析 + prompt loader context。→ typecheck + test。
7. **`acp/session-strategies.ts`**——搬运分发器 + 4 运行器 + `createSharedPromptRunner` + 期望修复循环 + 文本累加。→ typecheck + test。
8. **瘦化入口**——`acp-agent.ts` 仅留 `acpAgentAction` + `restoreAgentToolNoise` + 5 再导出；删除已搬走的符号与无用 import。→ typecheck + test。
9. **测试拆分**——按 D4 把 `acp-agent.spec.ts` 的 describe 块迁入 `tests/acp/` 下 4 文件；保持断言不变。→ test。
10. **度量收尾**——scc 复核圈复杂度脱离前三；删除遗留死代码；最终全量 `npm run typecheck -w packages/runner && npm run test:run -w packages/runner`。

**回退策略**：每步独立 commit；任意步回归即 `git revert <step>` 回到上一绿点，不影响其余模块（模块间单向依赖，撤回顶层不影响底层）。

## Open Questions

- **`buildLivenessEventPayload` 归属**：按"payload 构造器集中 session-events"规则应入 `session-events.ts`；但因 liveness→session-events 已是单向，亦可留在 `liveness.ts` 保持其自洽。倾向留 session-events 求一致，实现时定（不影响无环性）。
- **测试 main 块（930 行）是否需二次切分**：它覆盖 4 个运行器，可能按运行器再拆为 4 文件。本次先整体迁为单 `session-strategies.spec.ts`，若仍超健康阈值再在 tasks 阶段细拆。
- **scc 可用性**：若环境无 scc，验收"复杂度脱离前三"的证据口径需在 tasks 阶段与评审确认（见 Risks）。
