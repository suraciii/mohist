# REPORT-331 —— Workspace Web UI 观察与管理面

Issue: #331「Workspace Web UI 观察与管理面」。本实现复用了 #328 落地的 workspace
API（列表 / 详情 / close），Web 面按既有 FSD + TanStack Query + design tokens 模式
新建 workspace 实体与两个页面，并让 session 详情显示其 workspace。

## 验收逐条证据

### 1. 列表页展示 project 内 active / archived workspace 及关键字段 ✅

**页面**：`packages/web/src/pages/workspaces/ui/WorkspacesPage.tsx`
- Active / Archived 两个分区（Archived 默认折叠，仿 Epics 页的 Done/Closed 折叠模式）。
- 卡片字段：name、origin badge（issue #N / Slack / Web / Manual）、status badge（active/archived）、
  当前绑定 session 数（`boundSessionCount`）、物化 runner（Home = runnerId + path，
  未物化显示 "Not materialized"）、createdAt / archivedAt。点击卡片跳转详情页。

**Web 测试**：`packages/web/src/pages/workspaces/ui/WorkspacesPage.test.tsx`
- `renders active workspaces with origin, status, bound session count, and home`：
  断言 `Active (2)`、`Issue #14`、`Manual`、`2 bound sessions`、`runner-a`、`/ws/pay`、
  `Not materialized`、`2026-01-01`。
- `shows archived workspaces in a collapsed section that expands`：断言 `Archived (1)`、
  折叠时卡片不可见、展开后可见且带 `archivedAt 2026-02-01`。
- `navigates to the workspace detail page on card click`：点击卡片进入详情路由。

**Server 测试**：`packages/server/tests/Mohist.Server.SpecTests/Specs/Workspace/WorkspaceEntityApiSpecs.cs`
- `List_ReturnsNameOriginStatusHomeAndBoundSessionCount`：断言 status=active、origin.kind=manual、
  repositories、`boundSessionCount=1`、home 缺省、createdAt 存在。
- `List_ArchivedWorkspaceCarriesArchivedAtAndZeroBoundSessions`：断言 archived + `boundSessionCount=0` + archivedAt。

### 2. 详情页展示仓库成员与绑定 session，并可相互跳转 ✅

**页面**：`packages/web/src/pages/workspace-detail/ui/WorkspaceDetailPage.tsx`
- Repositories（chips）、Bound sessions（列表行 → Link 到 `/sessions/:sessionId`）、
  Home（runnerId + path / Not materialized）、Created / Archived 时间、返回列表链接。

**Web 测试**：`packages/web/src/pages/workspace-detail/ui/WorkspaceDetailPage.test.tsx`
- `renders repositories, home, created/archived times, and bound sessions that link to session detail`：
  断言 repo chips `['server','web']`、session 行显示 `Reviewer` + `active · model-x`、
  点击 session 行进入 `/sessions/:sessionId` 路由（`session-target`）。

**Server 测试**：`WorkspaceEntityApiSpecs.Detail_ReturnsBoundSessions`：详情返回非空
`sessions`，且 `boundSessionCount == sessions.Count`。

### 3. Web UI 可执行 close；有活跃会话时错误可见且含下一步 ✅

**页面**：`WorkspaceDetailPage.tsx`
- active workspace 显示 Close 按钮 → AlertDialog 确认 → `POST /workspaces/{name}/close`。
- 成功：`useCloseWorkspace` 失效 `['workspaces']` 查询族并 toast；页面 refetch 后转为
  archived，按钮消失。
- 拒绝（409 `workspace_has_active_sessions`）：内联 `role="alert"` 错误框
  （`workspace-close-error`）显示服务端 message（"has N active bound session(s)"）+
  `details.hint` 下一步（"Stop or wait for the bound sessions to finish, then retry…"）。
  错误不依赖 toast，持续可见。

**Web 测试**：
- `archives the workspace after confirming close`：确认后 close 按钮消失、显示 archivedAt。
- `shows the rejection with next step when close is refused due to active bound sessions`：
  断言错误框含 `"Workspace 'pay-refactor' has 2 active bound session(s)."`，且 hint 文本
  `Stop or wait for the bound sessions to finish, then retry` 可见；close 按钮仍保留。
- `renders an archived workspace without a close action`。

**Server 测试**：
- `WorkspaceEntityApiSpecs.Close_ActiveBoundSession_ReturnsConflictWithNextStepHint`：
  409 + code `workspace_has_active_sessions` + hint 含 `mo session list --workspace`。
- `WorkspaceEntityApiSpecs.Close_NoBoundSessions_ArchivesWorkspace`：close 成功返回 archived。

错误语义来自 #328 已实现的 `WorkspaceGrain.CloseAsync`（`workspace_has_active_sessions`
+ hint），Web 只消费既有错误契约，未改 server 错误行为。

### 4. session 详情显示其 workspace ✅

**改动**：
- `packages/web/src/entities/coder-session/model/types.ts`：`UnifiedSessionContextRefsDto`
  补 `workspaceName`（server 早已在 wire 上输出，仅类型未声明）。
- `packages/web/src/pages/session/data/useUnifiedSessionDataSource.tsx`：`buildMetadata`
  用 `contextRefs.workspaceName` 填充 `SessionMetadata.workspace`。
- `packages/web/src/pages/session/ui/SessionDetailShell.tsx`：SessionHeader 的
  source-context 行在存在 workspace 时渲染 `session-workspace-link` → `/workspaces/:name`。

**Web 测试**：`packages/web/src/pages/session/ui/UnifiedSessionPage.test.tsx`
- `shows a workspace link in the source context when the session is bound to a workspace`：
  断言链接文本 `Workspace: issue-42`、href `/Test/workspaces/issue-42`。
- `omits the workspace link when the session carries no workspace reference`。

## 改动清单

**Server（最小补字段，理由见下）**
- `packages/server/src/Mohist.Server/Workspace/Services/WorkspaceReadModels.cs`：
  `WorkspaceDto` 增 `int BoundSessionCount = 0`。
- `packages/server/src/Mohist.Server/Workspace/Services/WorkspaceQuerier.cs`：
  新增 `CountBoundSessionsAsync`（与详情 Sessions 同源的 workspace label 查询）；
  `ListAsync` 逐行填充 `boundSessionCount`，`GetAsync` 同步填充。
- `packages/server/tests/Mohist.Server.SpecTests/Specs/Workspace/WorkspaceEntityApiSpecs.cs`：
  新增 5 个 API spec（列表字段 / archived / 详情 sessions / close 拒绝 + hint / close 成功）。

**CLI（契约同步，被 CliFieldContractTests 强制）**
- `packages/cli/Mohist.Cli/ResourceOutput.cs`：workspace 输出目录补 `boundSessionCount`，
  与 server DTO 对齐；`mo workspace list` 表格因此多一列绑定会话数（与 docs/workspaces.md
  "观察：来源、仓库、当前绑定的会话" 一致）。

**Web**
- 新实体 `packages/web/src/entities/workspace/`：`model/types.ts`、`model/origin.ts`
  （origin label，含单测 `origin.test.ts`）、`api/client.ts`（list/detail/close）、
  `api/queries.ts`（`useWorkspaces` / `useWorkspace` / `useCloseWorkspace`，含
  `closeWorkspaceMutationOptions` 单测 `queries.test.ts`）、`index.ts` 公共 API。
- 新页面 `packages/web/src/pages/workspaces/`（列表页 + 测试）、
  `packages/web/src/pages/workspace-detail/`（详情页 + 测试）。
- `packages/web/src/app/App.tsx`：路由 `workspaces`、`workspaces/:name`（lazy）。
- `packages/web/src/widgets/app-shell/ui/AppSidebar.tsx`：导航项 Workspaces（epics 之后，
  `FolderGit2` icon）；`AppSidebar.test.tsx` 规范顺序断言同步更新。
- `packages/web/src/widgets/app-shell/ui/Header.tsx`：页面标题映射（Workspaces /
  Workspace <name>）。
- session 详情 workspace 字段（见验收 4 的 3 个文件 + 测试）。

## 验证证据

- `npm test`（根）：exit 0。server-unit 2125 ✅ / server-spec 4046 ✅（含新增
  WorkspaceEntityApiSpecs 5 例）/ server-arch 51 ✅ / workflow-definition 175 ✅ /
  cli 1664 ✅ / runner vitest ✅ / test-duration 守卫 ✅。
- `npm run typecheck -w packages/web`：✅。
- `npm run test:run -w packages/web`：371 files / 4716 tests 全绿。
- `npm run check:fsd -w packages/web`：498 production modules 无边界违规。
- `npm run check:test-boundaries -w packages/web`：✅。
- `npm run build -w packages/web`：✅（server csproj 的 BuildWebAssets 亦内嵌此构建）。

## Rebase 后 SpecTests 红：诊断记录（2026-08-07）

分支 rebase 到含 #335 的最新 master 后，主 agent 全量 `npm test` 观察到 2 个
SpecTests 失败。诊断结论：**两个失败均为既有负载敏感 flake，与 #331 改动无关，
无域内代码可修**。

### 失败测试与根因

1. `SlackDmNewTaskIngressSpecs.New_task_does_not_cancel_prior_running_work`
   （`MohistIntegration` collection）：`AcceptLaunchAsync` 以 5 秒有界轮询等 runner
   认领 AgentJob，全量并行负载下 dispatch 传播超窗。与 #345 后 CI 红的 SlackDm
   同族（#351 已把单次 poll 改为收敛轮询，但负载敏感仍可超时）。
2. `GitHubWriteBackSpecs.Cancelled_WithReason_PostsCancelCommentWithReasonAndClosesNotPlanned`
   （`GitHubFeed` collection）：`PumpAsync` 两次 `DispatchNowAsync` 后 GitHub 评论
   尚未投递，`Assert.Single` 空集合。AGENTS.md 已记录为已知 flake（约 20-40%/run，
   PR #337 在修）。

### 证据

- 4 轮全量 SpecTests（apphost `-reporter json`）：2 轮全绿（4027/4027），2 轮各恰
  好 1 个上述 flake 失败（两轮失败测试各不相同——间歇签名）。
- 两 flake 类隔离跑（无并行负载）：SlackDm 3 轮全绿、GitHubWriteBack 4 轮全绿。
- 两个失败测试均不触碰 workspace 代码路径（Slack DM ingress / GitHub write-back），
  不经过 `WorkspaceQuerier`、workspace 端点或新 spec；新增 WorkspaceEntityApiSpecs
  5 例在全部 4 轮中均通过。
- rebase 前后测试数差异（SpecTests 4046→4027、UnitTests 2125→2103）为 #335 退役
  Managed worktree 测试所致，非本分支引入。

### 处置

按 AGENTS.md Broken-CI 决策树属「根因不在域内 / 已知 flake」：GitHubWriteBack 有
在飞 PR #337 修复；SlackDm 轮询已由 #351 收敛，继续放宽超时属掩盖而非修复。
本分支不代修其他域的测试。最终验证：rebase 后 `npm test` exit 0（server-spec
4027/4027 全绿）+ web typecheck/test 全绿。

## 风险与备注

- **server 补字段理由**：列表端点缺"当前绑定 session 数"，而该数字无法由既有列表
  payload 推导（detail 才返回 Sessions）。补法最小：一个 DTO 字段 + 每个 workspace
  一次 label count 查询（项目内 workspace 数量级很小，无性能担忧）。其余全部复用
  #328 端点，未改任何路由/grains。
- **boundSessionCount 语义**：绑定 session 总数（与详情 Sessions 列表同源）；与
  close 守卫使用的"active bound"计数不同——后者体现在 close 错误文案里，两者不混淆。
- **docs 未改**：`docs/web-ui.md` 的"页面一览"未列入 Workspaces 页。按 spec 先行与
  角色边界（docs 默认委派），本 issue 未动文档；如需把新页面写进页面一览，建议由
  文档 issue 跟进。
- 已知 flake（AGENTS.md 记录的 IssueCompositeLifecycleGrainSpecs 等）与本次改动无关；
  本次全量 `npm test` 两次均绿。
