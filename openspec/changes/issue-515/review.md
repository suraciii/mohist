# Issue 515 Review

## Findings

### [P1] 根消息启动没有把 thread 根 ts 传到终态投递

位置：`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1390-1391`

`LaunchChannelRootAsync` 已经计算出 `rootTs`，但创建 `ConnectionLaunchOrigin` 时仍传入
`body.ThreadTs`。频道根消息的 `body.ThreadTs` 是 `null`，所以根消息启动的 Session
没有 `SlackThreadTs` 标签，`AgentJobLineage` 和 `SlackTerminalDeliveryHandler` 最终
也会生成 `ThreadTs = null`。接受确认虽然发到 thread，最终结果却会被 adapter 发到频道根，
且该 Session 的 thread provenance 不完整。违反 issue AC1、AC7 以及
`channel-thread-routing` 的终态 thread 投递要求。

### [P1] 同 workspace 跨 Project 的多 Agent thread 会被错误地当成单 Agent

位置：`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1071-1072`、
`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackThreadSessionMappingStore.cs:98-101`

Bot 解析是 workspace-scoped 的，但 thread binding 列表仍按当前 ingress 的 `projectId`
过滤。若同一 Mohist Server 中的两个 Connection 位于不同 Project，却绑定同一 workspace、
channel 和 thread，那么 A 的 ingress 只能看到 A 的 binding，B 的 ingress 只能看到 B 的
binding；同一条未提及的回复会分别被两个 Agent 当作自己的单 Agent follow-up，而不是只提示
一次并停止工作。违反 issue 对多 Agent 歧义的要求、AC4，以及 T-003 的多 Agent 路由验收。

### [P1] 频道路径缺少 backpressure 拒绝门禁

位置：`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1059-1072`

DM 路径在 `:858` 会在 Connection 已因 backpressure 降级时停止接受输入，但频道路径没有同等
检查。`SlackOutboxStore.EnqueueRequiredAsync` 在容量达到上限时仍会写入 required delivery
并把 Connection 标记为 degraded；之后的频道根消息和 follow-up 仍可先创建 Agent work，
继续把 outbox 推过容量上限。这样频道入口无法实现设计和现有可靠性契约要求的“进入
Backpressured 后停止接受新的 Slack 输入”。

### [P1] follow-up 重投可能永久丢失接受确认

位置：`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1446-1449`

频道 follow-up 在 `AcceptFollowupAsync` 已成功、但确认消息入队前发生 Server 重启时，重投会
命中已有 inbox row，然后直接 `MarkDispatchedAsync` 并返回，不再入队确认消息。结果是输入已经
进入原 Session，但用户永远收不到该输入的 accepted/queued 状态。该路径应像根消息和 DM 路径
一样使用同一 `dispatchRef` 幂等重建确认，满足 thread acknowledgement 与 restart/redelivery
要求。

### [P1] 带显示名的 Bot mention 会绕过 bare-mention 检查

位置：`packages/mohist-slack/src/adapter.ts:188-192`、
`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:436-441`

adapter 的 mention 正则明确接受 `<@U123|bot-name>`，但 Server 的 `RemoveBotMention` 只移除
精确的 `<@U123>`。因此 Owner 只发送 `<@Bot|bot-name>` 时，剩余 mention token 被当成任务文本，
会创建 AgentJob，而不是回复“请提供任务”并且不创建资源，违反 bare mention 场景的验收。

### [P1] ambiguous 分支在 Owner 检查前会给非 Owner 发选择提示

位置：`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1074-1079`、
`1141-1151`

多 Bot 提及和多 Agent thread 的无 mention 回复都会先调用
`HandleAmbiguousPromptAsync`，在后续 Owner 检查之前创建 workspace prompt row 并入队选择提示。
所以非 Owner 的频道提及/回复不会收到明确拒绝，而可能触发 Bot 选择提示。issue 明确规定频道
调用资格为 Owner only，且非 Owner 提及或 bound-thread reply 必须明确拒绝；T-002 也将该行为列
为验收条件。歧义与权限的优先级需要在入口中按该契约处理并补测试。

### [P2] ambiguous prompt 的 claim 与 outbox 写入之间存在丢提示窗口

位置：`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs:1329-1337`、
`packages/server/src/Mohist.Server/Infrastructure/Slack/SlackAmbiguousPromptStore.cs:79-106`

`TryClaimAsync` 先持久化 first-writer-wins row，随后才写 outbox。如果进程在两步之间崩溃，
同一消息重投会看到已存在的 claim 并直接 no-op，选择提示永远不会产生。规范场景要求对
ambiguous message 发出一次选择提示；当前实现只保证“不重复”，不保证该必需反馈在重启/失败后
最终出现。

### [P2] issue 要求的实现差距文档没有随落地更新

位置：`docs/agent-connections.md:381-384`、`design/slack-agent-connection.md:161-166`

T-003 验收明确要求更新这两处 implementation-gap。当前产品文档仍写着 Slack Agent 及以上
行为尚未实装，设计文档仍写着第 4 步尚未开始、没有频道/thread 路由和多 Agent 归属判定，
与当前提交已经落地的行为相矛盾。

## Verification

- `npm run typecheck -w packages/mohist-slack`：通过
- `npm run test:ci -w packages/mohist-slack`：8/8 通过
- `dotnet test packages/server/tests/Mohist.Server.SpecTests/Mohist.Server.SpecTests.csproj --no-restore`：3532/3532 通过
- `dotnet test packages/server/tests/Mohist.Server.UnitTests/Mohist.Server.UnitTests.csproj --no-restore`：1672/1672 通过
- `dotnet test packages/server/tests/Mohist.Server.ArchTests/Mohist.Server.ArchTests.csproj --no-restore`：51/51 通过

<promise>FAIL</promise>
