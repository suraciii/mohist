## Why

当方向不对时，用户目前只能等一个错误的 Turn 跑完。今天的 `cancel` 入口只对一个正在执行的 Runtime 发起中断请求：撤不掉尚未开始的 Turn，按 Session 而非 Turn 寻址（点到过期入口可能误停后来的工作），停止结果无法确认时也没有一致的「停止请求中 / 已停止 / 未知」解释。需要把「取消排队」和「请求停止执行」拆成两种承诺，并让停止结果未知成为一个正式、可核对的状态，而不是被自动重试或伪造 idle 掩盖。

## What Changes

- 把停止动作的作用目标从 Session 收窄到具体 AgentTurn：取消或停止只影响指定 Turn，作用在已终结 Turn 上的过期入口返回「该 Turn 已结束」，不影响其后开始的 Turn。
- 引入取消（Cancel）：尚未开始执行的 AgentTurn（`Queued`）可被确定性取消，直接在 Server 侧翻转为 `Cancelled`，不接触 Runtime、不等待收敛。
- 把现有运行时中断重述为停止（Stop）：对 `Executing` 的 AgentTurn 发出的是停止请求而非立即生效；是否达成取决于 Runtime 何时收敛，停止确认不可得时 Turn 与 Session activity 保持 `Unknown`，不自动创建新 Turn、不重放已接受的 SessionInput、不伪造 idle。
- 首个 AgentTurn 被取消时由 AgentJob 裁定终结结果为 cancelled；后续 Turn 的取消与停止不改写已终结的 AgentJob。Turn result 与 AgentJob status 保持为独立事实。
- 已取消或已停止的 Turn 保留其已产生的记录与 transcript，不被删除（已是当前不变量，本变更显式维持）。
- Web 与 CLI 对「已取消 / 停止请求中 / 已停止 / 未知」使用同一套状态解释与入口；CLI 在 `mo session` 下提供与停止语义区分的取消入口。

## Capabilities

- `agent-turn-cancel`: 取消一个尚未开始执行的 AgentTurn——确定性翻转 `Queued → Cancelled`，不接触 Runtime；首个 Turn 的取消由 AgentJob 裁定为 cancelled 终结，后续 Turn 的取消不改写已终结 AgentJob。
- `agent-turn-stop`: 对执行中的 AgentTurn 请求 Runtime 停止——请求而非立即生效；停止结果不可确认时 Turn 与 Session activity 保持 `Unknown`，不创建新 Turn、不重放 SessionInput、不伪造 idle。
- `agent-turn-control-surface`: 取消与停止命令以 Turn 为作用目标，仅影响指定 Turn；作用于已终结 Turn 的过期入口返回「该 Turn 已结束」而非误停后续工作；Web 与 CLI 对 cancelled / stop-requested / stopped / unknown 使用同一套状态解释与入口。

## Impact

- **Server**（`packages/server/src/Mohist.Server/Sessions/`, `Agent/`, `Api/`）：`AgentSession.Transitions` 需把 Cancel/Stop 从仅有的「InitialTurn」路径推广到任意 Turn，新增 `Queued → Cancelled` 确定性分支；`AgentSessionCancelRoutes` / `ResolveCancelTargetAsync` 需接受 Turn 目标并加入 stale-guard；`AgentJobGrain.EnterTerminalStateAsync` 的终结映射需让首个 Turn 取消落到 cancelled 而非 Failed（`AgentJobStatus` 当前无 `Cancelled`，需新增或以 Failed + cancelled category 表达）；`AgentTurnStatus` 已含 `Cancelled`/`Unknown` 可复用。
- **Runner**（`packages/runner/src/server/cancel-handler.ts`, `runtime/`）：现有 `CancelAgentSession` 中断路径承担 Stop 角色，需按 Turn 目标寻址并仅报告执行事实；Cancel 分支不下发到 Runner。
- **Web**（`packages/web/src/entities/agent/`, `widgets/coder-session/`）：新增 Turn 级取消/停止入口与状态展示，复用统一词汇；当前 `SessionRecoveryActions` 仅含 Compact/Reset。
- **CLI**（`packages/cli/Mohist.Cli/MohistCliCommands.Session.cs`）：现有 `mo session cancel` 承担 Stop 语义，需区分取消与停止两个入口并输出统一状态。
- **Testing**：覆盖排队取消、执行中停止请求、停止结果 Unknown、过期 Turn 入口、首个 Turn 取消的 AgentJob 裁定与记录保留；使用 fake Runtime、可注入时间与存储边界，不触碰真实外部依赖。
