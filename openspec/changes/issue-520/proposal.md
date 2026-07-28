## Why

提交任务前，用户无法判断 Agent 现在到底能不能干活。当前一次 launch 总会被接受并立即进入 dispatch，但没有 Runner 或容量时 AgentJob 会在退避重试后以 `runner-unavailable` 终态失败；Agent 的执行定义是否完整也只能在启动失败后才知道。这两件事用户的下一步完全不同：配置缺口需要去补设置，暂时没资源只需要等待或排队。需要把「执行定义是否足以执行」（Readiness）和「现在能否开始」（Availability）分成两个独立结论，并让 `MaxConcurrentRuns` 真正作为对所有入口一致的调度闸门生效。

## What Changes

- 引入 Agent Readiness 结论：Ready / Needs setup / Unknown。Needs setup 指出可行动的配置缺口和修复入口；Unknown 表示 Mohist 暂时无法确认。Web、CLI 呈现 Mohist 给出的统一结论，不各自维护一套 Runtime 判断规则。
- 引入 Agent Availability 结论，与 Readiness 分开：Runner 离线、容量不足或达到并发限制属于 Availability，不把 Ready Agent 改成 Needs setup。
- 让 `MaxConcurrentRuns` 真正作为调度闸门对所有调用入口一致生效（含 launch 与 follow-up、Web / CLI / 事件路由 / 评论提及）：达到限制后提交的工作进入等待。该闸门是实时调度策略，不进入 Agent 执行定义快照，也不属于 AgentSession。
- **BREAKING（行为）**：AgentJob 在没有 Runner 或容量、或达到并发限制时，不再以 `runner-unavailable` 终态失败，而是进入可见的等待状态，直到容量可用或被显式取消。
- 调低 `MaxConcurrentRuns` 不会停止正在 active 的工作，也不改写或重启已有 AgentSession；调高后等待中的工作按新策略继续推进，无需用户重新提交。
- 等待中的工作对用户可见，并说明它在等什么（Runner 离线、容量不足还是并发限制）。

## Capabilities

- `agent-readiness`: 判断 Agent 执行定义是否足以执行，产出 Ready / Needs setup / Unknown 结论；Needs setup 给出可行动的配置缺口与修复入口。该结论只看执行定义，独立于 Runner 与容量。
- `agent-availability`: 判断当前是否有 Runner 和容量可以开始执行、或需要等待；该结论独立于 Readiness，涵盖 Runner 离线、容量不足与并发限制，并让等待中的工作及其等待原因对用户可见。
- `agent-concurrency`: `MaxConcurrentRuns` 作为实时调度闸门对所有调用入口一致生效——达限后工作进入等待而非失败；调低不停 active 工作、不改写已有 AgentSession；调高让等待工作按新策略继续推进。

## Impact

- **Server — Agent context** (`packages/server/src/Mohist.Server/Agent/`): 新增 Readiness 与 Availability 评估（读侧）；`AgentLauncher`、`AgentLaunchCoordinatorGrain`、`AgentJobGrain` 及 follow-up 的 `AgentSessionGrain` 路径需在 dispatch 前一致应用并发闸门；`AgentJobGrain` 取消 `runner-unavailable` 终态失败，改为等待。`MaxConcurrentRuns` 已在 `Domain/Agent.cs` 与 CRUD 全链路持久化，但尚未被任何调度逻辑读取。
- **Server — Runner/Workflow** (`packages/server/src/Mohist.Server/Runner/`, `Workflow/`): Runner 在线状态与每 Runner 的 slot 容量作为 Availability 的输入；等待状态的呈现可借鉴 `AwaitingApproval` 的 per-run surfacing 模式（当前「等待 Runner/容量」没有对应的首类状态，仅有聚合看板）。
- **Web** (`packages/web/src/entities/agent/`, Agent 配置页、启动与会话页): 呈现 Readiness 结论与缺口、Availability 结论，以及等待中工作的等待原因。
- **CLI** (`packages/cli/Mohist.Cli/`): `mo agent view` 显示 Readiness、Availability 与缺口；`mo agent launch` 在等待时返回等待状态而非失败。
- **Agent API 契约** (`design/agent-api.md` 状态边界表、`design/agent-execution.md`): Readiness / Availability 已是既定状态边界，本变更新增其与并发调度的实装；不改变状态裁判归属——Server 裁判，Runner 只报告执行事实。
- **Testing**: 用 fake Runtime / Runner / 存储与可注入时间覆盖达限排队、调低调高、Runner 离线、Readiness 缺口诊断与等待状态的断线续读；不访问真实外部环境，不使用墙钟。
