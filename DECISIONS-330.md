# 主 agent 决策 — Issue #330（回应 PLAN-330.md §6 的 D0–D5）

## D0（编号）— 非问题，无需修正
glm5.2 看的是 `openspec/changes/archive/...issue-330`（旧 openspec 产物），不是实时 GitHub issue。实时 GitHub #330（milestone #5）**就是**「Slack / Web 入口的 workspace 自动解析」（已 `gh issue view 330` 确认）。工作区名 `impl-ws-330-slack-web` 与之对应，PR/issue 关联用 #330。**无需 tracker 修正。**

## D1（slack channel 归档事件源 scope）— 采纳解读 A + 入口 hook (a)
- **本 issue = Server 侧归档动作 + 入口 hook + 可被 fake 驱动**。验收 3（channel 归档后 workspace 归档；下一条触发建全新 workspace）通过 **fake 驱动 Server 侧归档 hook** 满足，不依赖真实 Slack。
- 入口 hook 形态选 **(a) HTTP 路由**：复用 slack ingress 路由族，新增 `POST .../connections/{c}/channel-archive`（adapter 调用，带 lease 鉴权，body=`{teamId, conversationId}`）。
- **具体 slack archive 事件源适配**（`mohist-slack` adapter 订阅真实 Slack `channel.archive`/`group.archive`/`im.close` 事件并翻译为 Server hook）= **non-goal / 跟进 issue**。
- 归档语义：`ArchiveByOriginAsync` **不查活跃 session 守卫**（channel/conversation 归档是外部生命周期事件，区别于用户 `mo workspace close`）。

## D2（web conversationId 身份）— 采纳 conversationId = pre-minted sessionId
最小、忠于现有 web 模型（一个对话=一个 session=一个 workspace）。引入 web 客户端拥有的稳定 conversation UUID 列为后续。

## D3（slack workspace Name 可读性）— 采纳首版 `slack-{channelId}`
入口 payload 仅含 channelId；不在本 issue 改 adapter ingress 契约去携带 channel 显示名。留跟进。

## D4（交互 workspace 初始 RepositoryNames）— 采纳 `[]`（空）
空目录 + 按需 `mo workspace repo add`。最小授权原则。

## D5（web client context.workspace 清理）— 留后续
本 issue 仅 server 自动解析；web composer 的 workspace chip 失效清理留跟进。

---

## 实施纪律（给实现 agent）
1. 按 PLAN-330 子任务顺序：**A（Server 核心：grain CreateAsync/ArchiveByOriginAsync + InteractionWorkspaceProvisioner + DI）→ B（Slack 入口接线 + 归档 hook）→ C（Web 入口接线）→ D（子 session 继承）**。A 是 B/C/D 硬前置。
2. **零 schema 变更**：slack/web origin 的 kind/payload 列与 partial unique index、session `LabelWorkspaceName` 都已存在（#328/#329/#332 已建）。不要加迁移。
3. **复用 issue origin 模式**：照 `IssueGrain` 的 `EnsureIssueWorkspaceAsync`/`ArchiveByIssueAsync` 范式，提取通用 `CreateAsync`（`CreateManualAsync` 委托之，**保留公开签名不破坏 manual**）+ 新增 `ArchiveByOriginAsync`。
4. server：`npm test`（零警告）；slack/web ingress 用 fake（既有 `SlackRuntimeLeaseTestSupport` + HTTP ingress）；时间走 `_fixture.TimeProvider`。
5. 新 spec 类**一律用共享 `[Collection("MohistIntegration")]`**（不带自带 silo——#328 flake 教训）。
6. 测试硬约束（design/testing.md）：无真实 slack/web/网络/时间，可注入、确定性、不 flaky。
7. 模型简洁、默认不写注释。
8. spec 冲突/不确定 → BLOCKERS-330.md 停下回报。
9. 完成后写 REPORT-330.md：逐条验收（1-4）测试证据 + 改动清单 + 最终 `npm test` + 风险（含 D1 follow-up：真实 slack archive 事件源适配、D3 channel 显示名、D5 web chip 清理）。

base 是最新 master（含 #328/#329/#332/#335），分支 `impl/ws-330-slack-web`。
