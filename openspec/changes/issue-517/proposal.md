## Why

#514 交付了 Slack Connection 的最小垂直路径——装上、认领、派一次活、拿结果。但 Connection 一旦投入真实使用就会遇到凭据过期、人员离职、要临时停掉这类运维场景，而当前实装没有任何手段处理：凭据只能靠重跑 `configure` 静默覆盖（不同步验证、不拦截改绑 workspace/App/Bot）；Owner 认领是一次性的，`SlackOwnerClaimService.GenerateAsync` 在已有 Owner 时直接抛错，离职后无法转移；`DesiredState` 字段虽已存在且 adapter discovery 已按 Enabled 过滤，但没有任何路由或命令暴露启停开关；诊断面只是把四个原始事实字段原样返回，消费者要自己猜下一步该做什么，也没有 Owner 可用性与身份漂移检测。本 issue 让 Connection 在不删掉重建、不丢已接受任务的前提下可运维，让用户能看懂当前到底卡在安装、服务、凭据、Owner 还是 Agent 配置，并采取唯一下一步。

## What Changes

- 新增凭据轮换：用户可重新提交 App/Bot token 并立即同步重新验证；新凭据解析出的 workspace/App/Bot 与原绑定不一致时被拒绝，现有绑定保持不变，轮换不借机改绑。`configure` 对已验证 Connection 执行轮换语义（同步验证 + 绑定一致性校验），不再是无校验的静默覆盖。
- 新增 Owner 转移：操作者对已有 Owner 的 Connection 发起转移，生成新的短时单次认领码；新 Owner 在 Bot 私聊认领成功前旧 Owner 保持有效（原子交换）；离职、停用或 guest 身份的成员不会自动转给同名成员；转移认领复用既有 workspace 正式成员校验。
- 暴露 Disable/Enable：新增路由与 CLI 命令切换 `DesiredState`；Disable 后立即停止接受 Slack 输入和发送新回复（ingress 与 adapter discovery 双向拦截），但已接受执行仍由 Mohist 保存；Enable 后不回放禁用期间的消息或过期进度。Disable 是用户选择，Degraded 是外部能力异常，两者不互相顶替。
- 明确 Delete 边界：删除清理 Connection 专属凭据、接收进度（inbox）、会话映射与待发记录（outbox），不删除 Agent 与已接受的 Job/Session/Input/Turn/附件，诊断面不假装已从 Slack 卸载 App。（现有 `DeleteAsync` 已做级联清理，本 issue 确认其保留边界并补齐诊断措辞。）
- 新增统一诊断面：Web 与 CLI 对 Setup 未完成、服务离线、凭据失效、Owner unavailable、身份漂移和 Agent Needs setup 给出不同且可行动的诊断；汇总区只突出当前最重要的状态、原因与唯一下一步。诊断基于既有四类独立事实（SetupProgress/DesiredState/ConnectionHealth/AgentReadiness）+ 新增的 Owner 可用性与身份漂移两个维度计算，不引入新的覆盖性 `Connected` 状态。
- 新增身份漂移检测：Slack 侧返回的 App 名称/头像与 Connection 记录的 BotName/AvatarHash、以及与 Agent 名称之间出现漂移时如实显示差异，不自动改写 Slack 侧。

非目标（来自 issue）：积压、背压、Degraded（Backpressured）与 Delivery uncertain 的呈现与处理（由可靠性 issue 交付）；在 Slack 服务进程中持久缓存事件/thread 映射/待发消息；公开 App Marketplace、多租户托管、计费或跨组织身份联邦；自动修改 Slack App 名称/头像或从 Slack 卸载 App。

## Capabilities

- `connection-credential-rotation`: 凭据轮换作为受控操作——重新提交 App/Bot token 后同步重新验证（复用 `SlackSetupVerifier` 的 workspace/App/Bot 一致性与必需权限校验）；新凭据不属于原 workspace/App/Bot 时被拒绝且现有绑定不变；轮换不触发改绑、不重置已接受的 Owner 与工作；CLI `rotate-credentials` 与 API 路由暴露此操作，凭据仍走受保护输入/`--credentials-file`，不接受命令行 token。
- `connection-owner-transfer`: Owner 转移与不可用处理——操作者对已有 Owner 的 Connection 发起新一轮认领（短时单次码），新 Owner 认领成功前旧 Owner 保持有效，认领成功时原子交换；转移认领复用 workspace 正式成员校验，离职/停用/guest 身份不会自动转给同名成员；Owner unavailable（成员已离开/停用/降级为 guest）作为可诊断状态暴露但不自动触发转移。CLI `transfer-owner` 与 API 路由暴露此操作。
- `connection-lifecycle-control`: Disable/Enable/Delete 的用户驱动语义——新增路由与 CLI `enable`/`disable` 切换 `DesiredState`；Disable 立即停止接受 Slack 输入与发送新回复（ingress 拒绝 + adapter discovery 不再发现该 Connection），已接受执行仍由 Mohist 保存；Enable 不回放禁用期间消息或过期进度；Disable（用户选择）与 Degraded（外部能力异常）独立不互替；Delete 清理 Connection 专属凭据/inbox/映射/outbox，不删除 Agent 与已接受的 Job/Session/Input/Turn/附件，不假装已从 Slack 卸载 App。
- `connection-diagnostics`: 统一诊断面——把 Setup progress、服务在线性（heartbeat 新鲜度）、凭据有效性、Owner 可用性、身份漂移与 Agent Readiness 汇总为「当前最重要状态 + 原因 + 唯一下一步」的可行动诊断；Web 与 CLI 各自呈现该汇总，区分安装未完成/服务离线/凭据失效/Owner unavailable/身份漂移/Agent Needs setup 并给出不同下一步；身份漂移如实显示 Slack App 名称/头像与 Agent 名称的差异，不自动改写 Slack 侧。

## Impact

- **Server**（`packages/server/src/Mohist.Server/`）:
  - `SlackConnectionRoutes.cs`: 新增 `POST /{id}/rotate-credentials`（或扩展 `configure` 语义）、`POST /{id}/transfer-owner`、`POST /{id}/disable`、`POST /{id}/enable`；ingress 路由增加 `DesiredState == Disabled` 拦截（返回明确拒绝，区别于 backpressured 的 409）。
  - `SlackSetupVerifier.cs`: 支持同步轮换验证路径（当前仅在 adapter-session heartbeat 异步触发）；轮换失败回滚至原凭据。
  - `SlackOwnerClaimService.cs`: `GenerateAsync` 放宽「已有 Owner 即拒绝」，区分初次认领与转移认领；新增 Owner 可用性探查（复用 `ISlackApiClient.UsersInfoAsync` 判定成员当前状态）。
  - `AgentConnectionStore.cs`: `UpdateAsync` 已支持 `desiredState`，需补 ingress/dispatch 层的 Disabled 运行时拦截。
  - 新增诊断计算（纯函数）：跨四类事实 + Owner 可用性 + 身份漂移产出「最重要状态 + 唯一下一步」，供 Web/CLI 复用。
- **CLI**（`packages/cli/Mohist.Cli/`）: `MohistCliCommands.AgentConnection.cs` 新增 `rotate-credentials`、`transfer-owner`、`enable`、`disable` 子命令；`view`/`list` 输出改为消费诊断汇总（唯一下一步）。当前 spec 测试显式断言这四个命令不存在（`CliAgentConnectionCommandSpecs.cs:18-21`），需更新。
- **Web**（`packages/web/`）: 新增 Connection 诊断视图（当前 Web 完全无 Slack/Agent Connection 代码），呈现汇总区与各维度细节。
- **mohist-slack adapter**（`packages/mohist-slack/`）: discovery 已按 Enabled 过滤，无需改动；Disabled 的 Connection 自然不再被发现与投递。
- **Runner**: 不变。
- **测试**: 覆盖轮换成功/绑定不一致被拒绝/轮换失败回滚、转移原子交换/旧 Owner 在新认领前有效/离职不自动转移、Disable 立即拦截 ingress 且保留已接受工作/Enable 不回放、Delete 保留边界、诊断对六类状态给出不同唯一下一步、身份漂移如实显示不自动改写；全部走 fake Slack（成员目录/状态变更/auth.test/bots.info）、fake adapter↔Server 传输与可注入时间（认领码过期、heartbeat 新鲜度），不触真实 Slack/网络。
- **文档/spec**: `docs/agent-connections.md`、`design/slack-agent-connection.md` 已描述运维动作与诊断目标状态，随实装推进收敛各自实装差距小节。
