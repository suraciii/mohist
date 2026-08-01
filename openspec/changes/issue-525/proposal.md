## Why

Slack Connection 的全部创建与推进步骤目前只在 CLI 里（`mo agent connection create` / `configure` / `claim-owner`）。Web 虽然能打开一个 Connection 的只读诊断页（`/connections/:id`），却无法创建、配置凭据或认领 Owner：`agent-connection` 实体只导出 `getConnectionDiagnostic` 一个读函数，Agent 详情页也没有任何 Connection 入口。结果是不在终端里工作的用户根本无法把 Agent 接入 Slack；而 Setup 虽然在服务端已是可恢复的持久事实（`SetupProgress` 状态机 + 加密凭据），用户中途被打断后没有一个 Web 上的地方能回到当前这一步继续。本 issue 让 Web 成为与 CLI 对等的 Setup 入口：从 Agent 详情页建 Connection、走完与 CLI 相同的步骤，关页面/换设备后接着走，而不是从头再来。

## What Changes

- 在 Agent 详情页新增 Connections 区（右栏，与 SubscriptionsSection 并列的注入式 widget），列出该 Agent 的 Connection 并提供 **Add Slack**。
- Web 可创建一条可恢复的 Slack Connection：立即得到将在 Slack 中出现的 Bot 身份预览（名称、头像、说明；Agent 名称不符合 Slack 命名规则时只预览带稳定后缀的 mention name，不改 Agent 本身）与 **Create in Slack** 入口，驱动既有服务端路由。
- 新增受保护凭据表单：Web 提交 App token（xapp-）/ Bot token（xoxb-）完成配置，token 不回显、不进入页面可见状态或日志、提交后不被 Web 读回；凭据仍由服务端 AES-GCM 加密保存。不提供把 token 写进 URL / query 的方式。
- Setup 由服务端 `SetupProgress` 单一权威驱动：关闭页面、刷新或换设备后已完成步骤保留，用户从当前这一步继续；`mohist-slack` 离线、token 无效或 Agent 尚未 Ready 时不丢失进度，只给出可执行的唯一下一步。Web 不自维护一份步骤状态。
- Connection 汇总区每次只突出一个当前状态与唯一下一步（复用既有 `connection-diagnostics` 的 `primaryState` / `nextAction`），同时仍可分别读出 Setup progress / Desired state / Connection health / Agent Readiness 四类事实。
- 同一个 Connection 在 Web 与 CLI 看到的是同一份进度：一侧完成的步骤在另一侧立即成立，两边可交替操作（Web 与 CLI 配置的是同一个 Agent 接入，不建立两份本机配置）。

非目标（来自 issue）：凭据轮换、Owner 转移、Disable / Enable / Delete 等运维动作（已在 #517 交付）；频道访问策略的管理界面（Allowlist 成员选择器、Anyone）；在 Web 中呈现 Slack 会话内容或 transcript；自动创建 Slack App 或代替用户在 Slack 侧完成安装。

## Capabilities

- `web-connection-setup`: Web 从 Agent 详情页创建并接管可恢复的 Slack Connection——Connections 入口与 Add Slack、由绑定 Agent 派生的 Bot 身份预览与 Create in Slack、以及由服务端 `SetupProgress` 单一权威驱动的可恢复 Setup（关页面 / 刷新 / 换设备后从当前步骤继续；服务离线 / 凭据无效 / Agent 未 Ready 不丢进度并只给唯一下一步）；汇总区突出单一当前状态 + 唯一下一步、四类事实仍可分别读出；Web 与 CLI 是同一份进度的两个入口。
- `web-credential-input`: Web 中以受保护方式捕获 Slack 凭据——App / Bot token 经受保护表单提交配置，不回显、不进入页面可见状态或日志、提交后不被 Web 读回；不接受把 token 放进 URL / query；凭据仍由服务端加密保存。

## Impact

- **Web**（`packages/web/src/`）:
  - 新增 widget（平行于 `widgets/agent-subscriptions`），经 `AgentDetailPage` 的 `components` 注入渲染到右栏（`pages/agent-detail/ui/AgentDetailPage.tsx` 右栏 `SubscriptionsSection` 附近）。
  - 扩展 `entities/agent-connection`：当前实体仅导出 `getConnectionDiagnostic`（只读）；需在 `api/client.ts` / `api/queries.ts` / `model/types.ts` 新增 create / configure（凭据）/ claim-owner 的 client 函数、TanStack Query mutation options 与请求 / 响应类型。
  - 新增受保护输入组件（`shared/ui`，当前 Web 无任何 password / secret 输入组件）；遵循 `design/web-ui.md`「Web 永不读取 Slack token」边界——只提交、不读回。
  - 新增 Setup 视图：以服务端 `primaryState` / `nextAction` 驱动渲染（不自建客户端步骤机），可借鉴 `entities/settings/ui/ProgressStages.tsx` 的有序状态呈现。
  - 测试：MSW handler + `components` / `dataHook` 注入（参照 `AgentDetailPage.readiness.test.tsx`、`connection-diagnostic/...test.tsx`）；`npm run typecheck`、`npm run test:run`、`npm run check:fsd`。
- **Server**（`packages/server/src/Mohist.Server/`）: create / configure / claim-owner / diagnostic 路由在 #514 / #517 已落地；但当前 `POST /slack-connections` 只回显调用方传入的 `BotName` / `AvatarHash`，未由绑定 Agent 派生身份预览（含 Slack 命名规则下带稳定后缀的 mention name、由 Description 生成的 App 短说明），需补齐这些只读派生字段以供 Web 渲染，不改接入绑定语义。
- **CLI / adapter / Runner**: 不变——CLI 与 Web 驱动同一服务端事实，`mohist-slack` 与 Runner 不感知 Setup 入口是 Web 还是 CLI。
- **测试**: 覆盖创建即得身份预览 + Create in Slack、受保护凭据提交不回显 / 不进页面状态、关页面 / 刷新后从当前步骤继续、服务离线 / 凭据无效 / Agent 未 Ready 只给唯一下一步、四类事实可分别读出、Web 与 CLI 同一进度一侧成立另一侧立即成立；全部走 fake（MSW / fake 服务端响应），不触真实 Slack / 网络。
- **文档 / spec**: `docs/agent-connections.md`（Web UI 步骤）与 `design/web-ui.md`、`design/slack-agent-connection.md` 已把 Web Setup 描述为目标，本 issue 是落地既有 spec，收敛各自实装差距小节。
