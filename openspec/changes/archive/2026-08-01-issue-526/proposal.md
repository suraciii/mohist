## Why

频道与 thread 的调用路由已就绪（#515），但频道调用资格被硬编码为 Owner only——
`SlackConnectionRoutes.cs` 在 5 处分支各自内联 `sender == connection.OwnerSlackUserId`，
非 Owner 一律拒绝。团队想把固定的 Agent 放进频道共享使用，却没有介于「只有我」和
「频道里任何人」之间的可控选择。产品 spec（`docs/agent-connections.md:313-347`）早已定下
Owner only / Allowlist / Anyone 三种策略及其语义，设计 spec（`design/slack-agent-connection.md`
访问控制决策与安全边界）也已明确「调用范围与执行能力是两件事」，但 Allowlist/Anyone 与频道
侧停止资格均未实装（`docs/agent-connections.md:387-392` 实装差距）。前置 issue（#515 设计 D7）
刻意不引入策略模型，把整块访问策略留给本 issue 从零落地。本 issue 让 Owner 能按需放宽谁能调用，
并补齐放宽后「谁能停别人发起的工作」的裁决。

## What Changes

- **新增访问策略模型**：`AgentConnection` 增 `AccessPolicy`（`owner_only` 默认 /
  `allowlist` / `anyone`）与允许成员列表存储；配套 EF 列、迁移与随 `DeleteAsync` /
  `IAgentConnectionProviderCleanup` 级联清除。Owner always 在允许集合内、不可移除。
- **统一调用裁决为单一决策点**：用一个评估器（接收 Connection、发送者 Slack 身份，Anyone
  另需频道可见性事实）替换 `SlackConnectionRoutes.cs:1187,1208,1287,1308,1325` 五处内联的
  owner 判等与 `:1491,1501` 拒绝 helper；无权成员收到明确拒绝且不创建 AgentJob /
  AgentSession / SessionInput。
- **Allowlist**：Owner 加上明确列出的工作区成员可调用；成员经稳定 workspace 身份（Slack
  user id）裁决，复用 `SlackOwnerClaimService.IsEligibleMember`（`SlackOwnerClaimService.cs:234-244`）
  校验为同一 workspace 内有效正式成员（拒 bot/deleted/guest/restricted）。
- **Anyone**：能证明属于 App 安装工作区、且能在当前频道看到 Bot（`ISlackApiClient`
  `ConversationsInfoAsync` 的 `IsMember`）的成员可调用；Slack Connect 外部参与者与身份不可
  确认者不触发；Bot 被邀进私有频道后仍按策略校验发送者。
- **私聊不变**：无论频道策略如何，一对一 DM 恒为 Owner only（已在
  `SlackOwnerClaimService.cs:148-150` 集中处理，本 issue 维持该边界）。
- **Manage access 操作面**：只有 Owner 用明确 Manage access 才能改策略与允许成员。放宽到
  Anyone 前，Owner 能看到「调用这个 Bot 等于借走该 Agent 已配置的仓库写入、工具与凭据」这一
  授权含义。新增管理 API 路由；CLI `connection edit` 增 `--access-policy` 与可重复
  `--allow-member`（Allowlist 下整体替换列表、不含 Owner，Owner-only/Anyone 下带 `--allow-member`
  报错先于变更）。
- **立即生效**：收紧策略后，此前有权成员的新调用立即被拒绝（包括已有会话的 follow-up），
  已接受的执行不被撤销、历史不删除。
- **成员失效**：成员离开 workspace、被停用或身份失效后不再视为有权成员，且不按同名成员自动
  接续（不按显示名/头像/消息文本判断授权）。
- **频道侧停止资格**：取消或停止某个 Turn 只能由 Connection Owner 或发起该 AgentSession 的
  Slack 成员执行（provenance 已记录 `MemberId`，`AgentSessionInputProvenance`）；其他被允许成员
  可继续对话，但不能停止别人的 Turn。

非目标（来自 issue）：通过访问策略削减或扩张 Agent 自身的 Runtime / Skills / 仓库 / 工具权限；
支持 Slack Connect 外部成员或 group DM；按频道/时间段/任务类型的细粒度授权；把 Slack 成员身份
变成 Mohist 管理员身份。

## Capabilities

- `channel-access-policy`: 频道与 thread 的调用裁决——Owner only / Allowlist / Anyone 三种策略
  下谁可调用固定 Agent，以及无权时明确拒绝且不创建任何 Agent 资源。含策略模型、允许成员存储、
  单一决策点（替换现有 5 处内联 owner 判等）、Anyone 的工作区成员 + 频道可见性证明、Allowlist
  成员的稳定身份校验、DM 在所有策略下恒为 Owner only、收紧后新调用立即被拒而已接受执行不撤销、
  成员失效后不按同名自动接续。
- `connection-access-management`: Owner 管理 Connection 调用范围的操作面——选择 Owner only /
  Allowlist / Anyone、经可识别的成员信息（稳定身份）增删 Allowlist、Owner 始终在内且不可移除、
  放宽到 Anyone 前看到等于授予该 Agent 全部已配置执行能力的安全说明、CLI `--access-policy` /
  `--allow-member` 与管理 API 的替换/互斥语义。
- `channel-session-stop`: 频道发起工作的停止资格——取消或停止某个 Turn 只能由 Connection Owner
  或发起该 AgentSession 的 Slack 成员执行；其他被允许成员可继续对话但不得停止别人的 Turn；
  频道侧停止入口与按 `MemberId` provenance 的资格裁决。

## Impact

- **Server — AgentConnection 域** (`AgentConnection.cs:3`, `AgentConnectionRow.cs`,
  `AgentConnectionStore.cs:12-20` 不可变字段清单, `:128 CreateAsync`, `:153 UpdateAsync`,
  `:241 DeleteAsync`): 新增 `AccessPolicy`（默认 `owner_only`）与允许成员列表；允许成员为独立
  子表（随 `IAgentConnectionProviderCleanup` 级联清除，参照 `SlackThreadSessionMappingStore`
  模式）。`UpdateAsync` 的不可变字段清单需放开策略/允许成员以支持 Manage access。
- **Server — EF / DbContext** (`MohistDbContext.cs:504-537`): 增列 + 允许成员表配置与迁移。
- **Server — ingress 裁判** (`SlackConnectionRoutes.cs:1160-1337` `HandleChannelIngressAsync`,
  `:1187,1208,1287,1308,1325` 五处 owner 判等, `:1491 RejectNonOwnerChannelMessageAsync`,
  `:1501 HandleAmbiguousNonOwnerAsync`): 引入单一访问决策点替换内联判等；Anyone 需在裁决时
  取 `ConversationsInfoAsync` 的 `IsMember` 与发送者工作区成员身份。DM 路径
  (`SlackOwnerClaimService.cs:148-150`) 维持 Owner only。
- **Server — 成员解析** (`SlackOwnerClaimService.cs:234-244` `IsEligibleMember`,
  `ISlackApiClient.cs` `UsersInfoAsync` / `UsersListAsync` / `ConversationsInfoAsync`):
  Allowlist 校验与 Anyone 成员证明复用既有谓词与 Slack 事实查询。
- **Server — 管理路由** (`SlackConnectionRoutes.cs` `/api/projects/{projectRef}/slack-connections`):
  `PATCH /{id}`（`:153`）当前仅接受 `botName`/`avatarHash`；新增 Manage access 端点处理策略选择
  与允许成员替换，含 Anyone 安全说明契约。
- **Server — 频道停止入口**: 频道侧当前无 stop/cancel 入口（#515 显式列为非目标）；本 issue
  新增频道停止表面与按 `AgentSessionInputProvenance.MemberId` 的资格裁决，复用 DM 侧
  `SlackConnectionRoutes.cs:812-873` 的 TurnControl 机制。
- **CLI** (`MohistCliCommands.AgentConnection.cs:339-363` `edit`): 当前仅 `--bot-name`/
  `--avatar-hash`；增 `--access-policy` 与可重复 `--allow-member`，落地 `docs/agent-connections.md:135,145-148`
  的替换/互斥语义。
- **Web** (`design/web-ui.md:81-83`): Connection 面板呈现访问策略与 Allowlist 编辑（成员 name/avatar
  搜索为面向人的控制，显示名不作为授权身份）、Anyone 安全说明。
- **Runner / Agent 执行**: 不变——按既有 AgentJob 协议执行，无 Slack 感知；策略只裁调用，不碰
  Agent 已配置的 Runtime / Skills / 仓库 / 工具。
- **依赖**: 无新外部依赖；复用既有 Slack API client、provenance、follow-up 与 TurnControl。
- **测试**: 走 fake Slack（`RecordingSlackApiClient` 的 `UsersInfo`/`UsersListAsync`/
  `ConversationsInfoAsync`）与可注入时间；覆盖 Allowlist（列出成员接受、未列出拒绝、Owner 恒接受）、
  Anyone（工作区成员接受、外部/guest 拒绝、Bot 不可见频道拒绝）、DM 恒 Owner only、无权不创建资源、
  收紧后新调用立即被拒而已接受执行不撤销、成员失效不自动接续、频道停止资格（Owner/发起者可、他人不可）；
  不触真实 Slack / 网络。
- **文档 / spec**: `docs/agent-connections.md:307-311,387-392` 与
  `design/slack-agent-connection.md:152-168` 已描述目标状态，本 issue 关闭 Allowlist/Anyone 与
  频道停止资格的实装差距，落地后无需改正文。
