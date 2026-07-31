## Why

Slack 私聊里用户不开 thread，习惯一句一句接着说——后面几句是补充刚才那件事，不是每句都开新工作。但当前 Server 把每条 DM 消息都当全新任务：`SlackConnectionRoutes` ingress 无条件调用 `LaunchConnectionAsync`，每次都建立新的 AgentJob + AgentSession（`SlackConnectionRoutes.cs:339`）。结果用户想补充上一句话，却开了第二项工作；想换个话题，没有明确入口；跑偏了想喊停，DM 里没有取消或停止操作。follow-up 与 Turn 控制机制其实已由 issue 521/522 建好（`AcceptFollowupAsync`、cancel/stop 路由、`AgentSessionGrain` Turn 分类），但 `AgentSessionQuerier` 的 follow-up 与 cancel 解析器显式拒绝 `agent-connection` source（`AgentSessionQuerier.cs:319-371`）——Slack 启动的 Session 既无法被 follow-up 定位，也无法被 cancel/stop 命中。前置 issue（514/521/522）均已 done，本 issue 让这些既有机制对 Slack DM 生效，把私聊变成一段连续、可控的对话。

## What Changes

- DM 对话维护一个 current AgentSession：Owner 的普通消息作为 follow-up 继续当前会话，不创建新的 AgentJob；只有第一条消息（无 current Session）或明确的 New task 操作才建立新的 AgentJob + AgentSession 并切换为该 DM 的 current Session。
- New task 切换不取消旧工作：旧工作继续执行到自然终结，它迟到的回复带上可辨认的工作身份（所属 Job/Session），不混入新任务的 current Session。
- Turn 执行中发来的消息被接受并排队，用户看到「已接受待处理」而不是「已经在跑」；当前 Turn 终结后排队消息按既有 follow-up 调度推进。
- Owner 可在 DM 中取消排队中的工作或请求停止正在执行的工作，复用 Mohist 既有 Turn 操作资格（cancel queued / stop executing），不另立一套规则。
- 过期的停止/取消入口不会误停后来开始的工作：操作只作用于它发出时那一轮，命中已终结的 Turn 时返回「该工作已结束」。
- 同一条 Slack 消息重复投递（含 adapter 或 Server 重启后）不产生第二条输入或第二项工作——既有 inbox 去重保护 launch 路径，follow-up 路径由 `AcceptFollowupAsync` 的幂等键保护。

非目标（来自 issue）：频道提及与 thread 中的会话归属；group DM 与多人私聊；自动判断用户是在补充还是在换话题；在 Slack 中呈现完整 transcript 或诊断工作台。

## Capabilities

- `dm-session-continuity`: DM 对话的 current AgentSession 生命周期——普通消息作为 follow-up 继续当前会话（不创建 AgentJob），New task 操作建立新 AgentJob + Session 并切换 current；切换不取消旧工作，旧工作迟到回复带可辨认的工作身份，不混入新任务；Turn 执行中的消息被接受排队而非拒绝；重复投递的同一条消息回到同一输入，不产生第二项工作。
- `dm-work-control`: Owner 在 DM 中取消排队中的工作或请求停止正在执行的工作，复用既有 Turn 操作资格；停止请求只作用于发出时那一轮，命中已终结 Turn 的过期入口返回「已结束」而非误停后来的工作。

## Impact

- **Server — ingress 路由** (`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs`): ingress 需在 launch 与 follow-up 之间分流——先查 DM 对话的 current Session，命中则走 follow-up，未命中或 New task 则走 launch。当前 `:339` 无条件 `LaunchConnectionAsync`。
- **Server — current Session 映射**: 新增 DM conversation → current AgentSession 映射存储（`design/slack-agent-connection.md:109-112` 已 spec，当前仓库无对应表或字段）；New task 操作在该映射上原子切换。
- **Server — Session 查询与解析** (`AgentSessionQuerier.cs:319-371`, `AgentSessionQuery.cs:165-208`): `ResolveCanonicalFollowupTargetAsync` 与 `ResolveCancelTargetAsync` 需支持 `agent-connection` source；`QueryRowsByLabels` 需支持按 Slack conversation identity 查询（当前 `SlackConversationId` label 被写入但查询时 fallthrough 到 `Where(_ => false)`，`AgentSessionQueryMetadataKeys.cs:15-17`）。
- **Server — follow-up 路径** (`AgentSessionFollowupRoutes.cs`, `AgentSessionGrain.cs:426-651`): 既有 follow-up 机制（`AcceptFollowupAsync`、`BeginNextFollowupDispatchAsync`、Turn 分配 `AgentSession.Transitions.cs:1087-1104`、幂等键去重）对 `agent-connection` source 生效；ack 回复需区分「继续对话」与「新工作」的措辞。
- **Server — Turn 控制** (`AgentSessionCancelRoutes.cs`, `AgentSessionStopRoutes.cs`, `AgentSession.Transitions.cs:681-803`): cancel/stop 路由对 `agent-connection` source 生效；需校验 Slack 发送者身份（当前路由无 Slack sender 授权）。
- **Server — 终态投递渲染** (`SlackTerminalDeliveryHandler.cs:61-77`): 迟到回复需带上工作身份（所属 Job/Session）让用户区分是哪项工作的结果。terminal delivery event 已携带完整 identity（`AgentJobLineage.cs:100-128`），只在渲染层呈现。
- **mohist-slack adapter** (`packages/mohist-slack/`): 无状态不变；若 New task 以 DM 内操作（关键词/命令）表达，envelope 规范化时识别该意图。
- **Runner / Web / CLI**: 不变——Runner 按既有 AgentJob 协议执行，无 Slack 感知；Web/CLI 的 follow-up/cancel/stop 不受影响。
- **测试**: 覆盖 follow-up 继续会话、New task 切换、旧工作迟到回复带身份、Turn 执行中排队与「已接受待处理」、cancel queued / stop executing、过期入口返回「已结束」、重复投递幂等；全部走 fake Slack 与可注入时间。
- **文档/spec**: `docs/agent-connections.md:204-273`（DM 使用）与 `design/slack-agent-connection.md:96-115`（DM current-session 规则）已描述目标状态，本 issue 关闭实装差距。
