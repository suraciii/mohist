## Why

Slack 私聊已能派活、追问与受控（#514/#521/#524），但团队真正协作的地方是频道与
thread，而当前 ingress 把一切非私聊消息直接丢弃（`SlackConnectionRoutes.cs:281`
`!body.IsDirectMessage → ignored`）。用户无法在频道里 @ 一个 Agent 发起工作，也无法
在同一 thread 里自然追问；多个 Agent 共存于一个讨论时没有归属规则，要么串上下文、
要么谁都不响应。产品 spec（`docs/agent-connections.md:200-265`）与设计 spec
（`design/slack-agent-connection.md:96-115`）已把 `Agent + thread` 定为频道会话边界，
并要求「归属无法判断时不启动工作」——这些目标状态均未实装。前置 issue 已就绪，本
issue 让 Slack Agent 从一对一私聊进入团队频道与 thread，并第一次处理多 Agent 同一
thread 的独立归属。

## What Changes

- 频道根消息明确 `@Bot` 时为该 Agent 建立新 AgentJob + AgentSession + 首条
  SessionInput，并把会话绑定到该根消息形成的 thread；Bot 在同一 thread 回复接受状态
  与最终结果。
- 对已绑定 thread 的人类回复作为 follow-up 继续该 AgentSession，不要求每条消息重复
  mention；复用 #521/#524 已建立的 follow-up 机制（`AcceptFollowupAsync` 与幂等键）。
- 一个 thread 可分别绑定多个 Mohist Agent，每个 Agent 拥有独立 Session 与独立上下文；
  第一次在已有 thread 中提及另一个 Bot 为它建立独立 Session，不切换也不污染原 Agent
  的会话。
- 归属无法判断时不猜测、不启动工作：一条消息同时 @ 多个由同一 Server 管理的 Bot，
  或 thread 已绑定多个 Agent 而回复未指明目标，都不触发任何 Agent，并只提示一次让
  用户明确选择。
- 收窄入站接受面：只有私聊、明确提及和已绑定 thread 的回复进入 Mohist；Bot 自己发送
  的消息、普通未绑定频道消息和身份不明的发送者不创建 Job、Session 或 Input。
- 每条被接受的输入都能回答它来自哪个 workspace、channel、thread 和成员（当前
  `ConnectionLaunchOrigin` 只带 DM conversation id，无 thread/channel 区分）。
- 频道调用资格仍为 Owner only：复用 `AgentConnection.OwnerSlackUserId`，非 Owner 在
  频道提及收到明确拒绝且不创建任何 Agent 资源。
- 频道与 thread 的重复投递（含 adapter 或 Server 重启后）回到同一 SessionInput，不产
  生第二项工作或第二条输入；重启后已绑定 thread 仍继续原 AgentSession。
- **`mohist-slack` 适配器**（`packages/mohist-slack/`）：envelope 规范化需识别 thread
  归属（`thread_ts`）与 mention，出站投递需在 thread 内回复；adapter 仍无状态，归属
  判断全部在 Server。

非目标（来自 issue）：Owner only 之外的访问策略（Allowlist/Anyone）及谁能停止别人
发起的工作；导入已有 thread 历史与 Slack 文件处理；Slack Connect 外部成员、group DM
或跨 Mohist Server 的多 Bot 协调；按自然语言/频道主题/上一位发言者猜目标 Agent；通过
访问策略增减 Agent 自身的 Runtime/Skills/仓库/工具权限。

## Capabilities

- `channel-thread-routing`: 频道根提及建立新工作并把会话绑定到 thread，thread 回复作
  为 follow-up 继续同一 AgentSession 且不必重复 mention；`Agent + thread` 是频道会话
  边界，一个 thread 可绑定多个 Agent 各自拥有独立 Session 与上下文，首次提及新 Agent
  不切换也不污染既有会话；thread 绑定持久化、重启后继续原会话；重复投递的同一条频道
  消息回到同一 SessionInput，不产生第二项工作。
- `channel-attribution`: 频道消息的接受面与目标 Agent 解析——只有私聊、明确提及与已
  绑定 thread 的回复进入 Mohist，Bot 自身消息、普通未绑定频道消息与身份不明发送者不触
  发；一条消息同时提及多个同 Server 管理的 Bot、或 thread 已绑定多 Agent 而回复未指明
  目标时都不启动工作并只提示一次明确选择；每条接受的输入记录 workspace/channel/thread/
  member 用于审计；本 issue 频道调用恒为 Owner only，非 Owner 收到明确拒绝且不创建
  Agent 资源。

## Impact

- **Server — ingress 路由** (`packages/server/src/Mohist.Server/Api/SlackConnectionRoutes.cs`):
  当前 `:281` 对非 DM 一律 `ignored`；需在「忽略 / 拒绝 / launch / follow-up」之间按频道
  场景分流——解析提及、判断 thread 是否已绑定、归属唯一时 launch 或 follow-up、归属不
  明时只回复一次选择提示。`SlackIngressBody`（`:973`）缺 `thread_ts` 与 mention 信息。
- **Server — thread 会话映射**: 新增 workspace + channel + thread →（Connection, AgentSession）
  映射存储，键为 Connection、Slack workspace、channel/conversation 与 thread 根消息 ts，既避免不同
  workspace 或频道的同 ts 串线，又支持同一 thread 多个 Agent（当前 `SlackDmSessionMappingStore` 唯一索引为
  `ConnectionId+DmConversationId`，无法表达多 Agent 同一 thread）；与 DM current-session 映射
  是两套语义（thread 无 New task 切换）。映射缺失时必须能由持久 inbox 路由或 Session provenance
  幂等修复，避免 launch 成功后重启把后续 thread 回复变成新工作或丢弃。删除 Connection 时随
  `IAgentConnectionProviderCleanup` 一并清除。
- **Server — 启动来源与标签** (`IAgentLauncher.cs:196` `ConnectionLaunchOrigin`,
  `AgentLaunchCoordinatorGrain.cs:320`, `AgentSessionQueryMetadataKeys.cs:15-17`): 需 区分 channel/thread 并写入 thread 标签以支持按 thread 查询会话；当前只有
  `SlackConversationId` 标签且值取自 `DmConversationId`。
- **Server — 多 Bot 归属解析**: 需按 workspace 解析一条消息/一个 thread 涉及哪些
  Connection（其 `BotUserId`），以判定单 Agent / 多 Agent / 多 Bot 提及；当前无跨
  Connection 的 workspace 级 bot 解析查询。
- **mohist-slack adapter** (`packages/mohist-slack/src/adapter.ts:152`, `types.ts:12`):
  `normalizeSocketEvent` 与 `SlackEnvelope` 需携带 `thread_ts`、解析后的 mention，以及可让
  Server 区分 human/Bot/unknown 的发送者事实；Bot 与 unknown 事件必须被确认并忽略，不能因缺
  少 `user` 无限重投。出站 `drain`（`adapter.ts:127`）需在 thread 内回复；adapter 仍无状态，
  归属判断全部在 Server。
- **AgentConnection 域**: 本 issue 不新增访问策略字段——频道 Owner only 复用既有
  `OwnerSlackUserId`（`AgentConnection.cs:21`）；Allowlist/Anyone 留待访问策略 issue。
- **Runner / Web / CLI**: 不变——Runner 按既有 AgentJob 协议执行，无 Slack 感知；Web/CLI
  的 follow-up/cancel/stop 不受影响。
- **依赖**: 无新外部依赖；复用既有 inbox/outbox/secrets 与 follow-up 机制。
- **测试**: 覆盖频道根提及 launch、thread follow-up 不必重复 mention、同 thread 多 Agent
  独立 Session 与互不污染、多 Bot 提示只发一次、多 Agent thread 未指明回复不触发、Bot
  自身/普通未绑定消息与不明发送者被确认后忽略、不同频道同 ts 不串线、launch 成功而绑定尚未
  写入时的重启修复、重复投递与重启续绑幂等、每条输入的 provenance、非 Owner 频道提及被拒；
  全部走 fake Slack（事件/mention/thread/成员目录）与可注入时间，不触真实 Slack/网络。
- **文档/spec**: `docs/agent-connections.md:200-265`（频道与 thread 使用）与
  `design/slack-agent-connection.md:96-115`（`Agent + thread` 边界与多 Agent 规则）已描述
  目标状态，本 issue 关闭该实装差距。
