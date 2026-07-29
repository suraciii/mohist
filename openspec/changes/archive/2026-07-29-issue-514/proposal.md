## Why

Mohist Agent 目前只能从 Web 和 CLI 使用，从未以同一身份出现在外部入口。Slack Connection 的产品 spec（`docs/agent-connections.md`）与设计 spec（`design/slack-agent-connection.md`、`design/agent-api.md`）已定稿但完全未实装——仓库里没有任何 Connection、Slack 或 provider 集成代码，`ServiceTarget` 只有 Server/Runner，CLI 没有 `agent connection` 子组。与此同时，Agent 启动的幂等与可恢复观察面刚由 #512 落地（`AgentLaunchCoordinatorGrain` 以稳定调用身份去重、`POST /api/projects/.../agents/.../sessions` 接受 `Idempotency-Key`），使「同一 Slack 消息始终回到同一 SessionInput」这条可靠性契约有了现成落点。本 issue 交付 Owner-only DM 的最小垂直路径——装上、认领、派一次活、拿到结果——作为 Slack 接入的第一段可独立验证的产品价值，也第一次证明 provider adapter 经 Connection boundary 调用 Agent API 这条边界成立。

## What Changes

- 引入 **AgentConnection** 作为 Agent 域的 Project-scoped 子资源：固定绑定一个 active Agent、一个 Slack workspace 与一个独立 Bot 身份；创建后不可改绑；同一 Agent 在同一 workspace 最多一个未删除 Connection；Connection 只持有绑定、访问策略与生命周期，不复制 Instructions/Runtime/Model/Skills 或并发限制。
- 把四类互不替代的事实分开持久化与呈现——Setup progress（外部安装是否完成）、Desired state（本 issue 恒为 Enabled）、Connection health（Slack 侧是否健康）、Agent Readiness（执行配置是否已知可执行）——不允许用一个 `Connected` 覆盖。Agent Needs setup 时拒绝新委托但 Connection 保持健康；Readiness Unknown 时接受并等待 Runner 验证；Runner 离线或容量满明确排队。
- 在 Server infrastructure 新增 Slack provider 集成记录（与 Agent/Session 域隔离）：受保护凭据存储（加密 App/Bot token，不出现在命令参数、回显、Instructions、日志或 transcript）、provider inbox（按稳定 Slack 消息身份去重、有界容量）、DM conversation mapping、有界 outbound outbox。删除 Connection 时清除这些记录，但不删除 Agent/Job/Session 或已接受输入。
- 新增无状态 **`mohist-slack`** 适配进程作为 CLI 托管服务（`mo install slack` / `mo service status slack`）：用 Socket Mode 收发 Slack wire payload，翻译成带稳定 provider 身份的规范化 envelope 交给 Server Connection boundary，再调用 Agent API；不持久化任何需要恢复的状态，重启后从 Server 续领未收敛的入站与出站。
- CLI 新增 `mo agent connection` 子组（本 issue 交付 `create`/`configure`/`claim-owner`/`view`/`list`/`edit`/`delete`；`rotate-credentials`/`transfer-owner`/`enable`/`disable` 为后续 issue）；凭据以隐藏输入或受保护 `--credentials-file`（UTF-8 JSON `{appToken, botToken}`，`chmod 600`，非符号链接）读取，不接受命令行 token 参数。
- Setup 分步推进且已完成步骤不丢失：Create app & add credentials →（服务离线时）Waiting for Slack service →（验证失败时）Fix Slack setup → Claim owner → Complete；身份验证核对 workspace/App/Bot 一致性与必需权限后才进入认领。
- Owner 认领用短时、单次认领码：配置者在 Bot 私聊中发送该码；只有当前 workspace 中仍有效的正式成员能成为 Owner，外部协作成员、Bot 与已停用成员不能认领；重新生成立即使旧码失效。认领成功后只有 Owner 能私聊该 Bot，其他成员收到明确拒绝且不创建任何 Agent 资源。
- DM 派活垂直路径：Owner 在私聊发送一条任务 → Server 经 Connection boundary 以稳定调用身份调用 Agent API 的幂等启动，建立 AgentJob/AgentSession/首条 SessionInput/首个 AgentTurn → Bot 先回复已接受/排队/明确拒绝 → 最终结果回到同一 DM 对话。Slack 重复投递同一条消息（含 adapter 或 Server 重启后）始终回到同一 SessionInput，不产生第二项工作或第二条输入。本 issue 只做「一条消息一次工作」；DM 连续对话、current Session 与 New task 切换、Slack 中取消/停止均为后续 issue。
- 出站投递有界且诚实：可替代的中间进度合并为最新状态，最终结果/明确失败/需用户操作的消息不静默丢弃；容量不足时 Connection 进入 Degraded（Backpressured）并停止接受新输入；Slack 未确认投递时显示 Delivery uncertain，不盲目重发。

非目标（来自 issue）：DM 连续对话与 New task 切换；Slack 中取消/停止；Web 创建或接管 Connection；凭据轮换、Owner 转移、Disable/Enable/Delete 运维动作；频道提及、thread follow-up、Allowlist/Anyone 访问策略（本 issue 私聊恒为 Owner-only）；Slack 文件、已有 thread 历史、链接抓取；Slack 原生 Agent 体验、公开 Marketplace、多租户；共享 Bot 自然语言猜目标 Agent。

## Capabilities

- `agent-connection`: AgentConnection 作为 Agent 域的 Project-scoped 子资源——固定绑定一个 active Agent、一个 Slack workspace 与一个独立 Bot 身份，创建后不可改绑，同一 Agent 在同一 workspace 最多一个未删除 Connection；Setup progress、Desired state、Connection health 与 Agent Readiness 作为四类互不替代的独立事实持久化与呈现；删除 Connection 清除 provider 集成记录但保留 Agent/Job/Session 与已接受输入，Agent 行为与执行配置不变。
- `slack-connection-setup`: CLI 驱动的分步 Setup——受保护凭据输入与存储、`mo install slack`/`mo service status slack` 托管 `mohist-slack`、workspace/App/Bot 一致性与必需权限验证、Waiting for Slack service 与 Fix Slack setup 状态、已完成步骤在服务离线或 Agent 未 Ready 时不丢失；短时单次 Owner 认领码配合 workspace 正式成员校验确立 Owner；认领完成后私聊恒为 Owner-only，非 Owner 收到明确拒绝且不创建 Agent 资源。
- `slack-dm-dispatch`: Owner-only DM 派活垂直路径——一条 DM 任务经 Connection boundary 以稳定调用身份调用 Agent API 幂等启动，建立 AgentJob/AgentSession/首条 SessionInput/首个 AgentTurn；Bot 回复已接受/排队/明确拒绝，最终结果回到同一 DM 对话；同一 Slack 消息身份重复投递（含重启后）回到同一 SessionInput，不产生第二项工作；本 issue 不交付 DM 连续对话与 New task 切换。
- `slack-provider-reliability`: Server infrastructure 在 at-least-once Slack 投递下的可靠性契约——provider inbox 按稳定 Slack 消息身份去重并有界、出站 outbox 有界且合并可替代进度而不丢弃最终结果/明确失败/用户操作、容量不足进入 Degraded（Backpressured）并停止接受新输入、投递未确认显示 Delivery uncertain 不盲目重发；以上在 adapter 与 Server 重启后成立，且不改变 AgentJob/AgentTurn 的执行结果裁判权。
- `mohist-slack-adapter`: 无状态 `mohist-slack` 适配进程作为 CLI 托管服务——Socket Mode wire protocol 与规范化 Connection envelope 之间的翻译，经 Server Connection boundary 调用 Agent API，不持久化 inbox/映射/待发/Agent 配置，重启后从 Server 续领未收敛项，瞬时并发受限但不拥有持久队列或产品级背压。

## Impact

- **Server**（`packages/server/src/Mohist.Server/`）:
  - 新增 AgentConnection 聚合与 grain（Agent 域，`Agent/`）：绑定、访问策略、Setup progress、四类事实、生命周期与 Project+workspace+Agent 唯一性。
  - 新增 Slack provider infrastructure（Server infra，与 Agent/Session 域隔离）：受保护凭据加密存储、provider inbox（按 Slack 消息身份去重）、DM conversation mapping、有界 outbound outbox、Delivery uncertain / Backpressured 状态。
  - 新增 Connection boundary API surface：供 adapter 提交规范化 envelope（单一 `/ingress` 路由由 Server 侧分类：Setup 未完成则拒绝、匹配认领码则认领、非 Owner 则拒绝、Owner 任务则派活）、租赁凭据并上报心跳（`/adapter-session`）、领取出站 intent 并回报投递结果（`/deliveries`）；dispatch 经新增的 `LaunchConnectionAsync` 入口复用 `AgentLaunchCoordinatorGrain` 的幂等启动机制（`Agent/Services/AgentLauncher.cs:122`）与 `Idempotency-Key` 契约（`Api/AgentSessionLaunchRoutes.cs:82`），不新建第二条启动路径，也不暴露单独的 dispatch 路由。
- **CLI**（`packages/cli/Mohist.Cli/`）:
  - 新增 `mo agent connection` 子组（`create`/`configure`/`claim-owner`/`view`/`list`/`edit`/`delete`），凭据走隐藏输入或 `--credentials-file`。
  - `MohistCliCommands.Service.cs:7` 的 `ServiceTarget` 增加 `Slack`；新增 `mo install slack` / `mo service status slack` / `mo update slack`。
- **新 `mohist-slack` 包**（TS，与 runner 同工具链）：Socket Mode 客户端、envelope 规范化、出站渲染；无持久状态。
- **Runner**（`packages/runner/`）: 不变——按既有 AgentJob 协议执行，无 Slack 感知。
- **Web**（`packages/web/`）: 本 issue 不交付 Web 创建/接管 Connection；可能获得只读 Connection 视图，留待后续 issue。
- **依赖**: `mohist-slack` 引入 Slack Socket Mode 客户端依赖；Server 引入凭据加密机制（当前仓库无加密 secret store）。
- **测试**: 覆盖重复投递去重、Setup 各步骤与服务离线恢复、Owner 认领与成员校验、非 Owner 拒绝、accepted/queued/rejected、最终结果回送、Backpressured 与 Delivery uncertain、adapter 与 Server 重启续领；全部走 fake Slack（事件入站/ack/重投/成员目录）、fake adapter↔Server 传输与可注入时间（认领码过期、outbox 重试），不触真实 Slack/网络。
- **文档/spec**: `docs/agent-connections.md`、`design/slack-agent-connection.md`、`design/agent-api.md`、`design/architecture.md`、`docs/self-host.md` 已描述此目标状态，随实装推进更新各自实装差距小节。
