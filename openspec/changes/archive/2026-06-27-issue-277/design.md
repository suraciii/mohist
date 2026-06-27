## Context

Epic 详情页在 320–430px 移动宽度下不可用，且顶部栏显示裸 `Epic #`。这是一次隔离的 Web 呈现层修复，不改 Epic lifecycle API/状态机、不改 linked issues 业务能力。动机见 `proposal.md`，需求契约见 `specs/epic-detail-responsive-layout/spec.md`。

涉及的两个呈现组件当前状态：

- `packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`：详情头部用 `<div className="flex flex-wrap items-start justify-between gap-4">` 把「标题/描述（`min-w-0 flex-1`）」和「动作按钮组（`flex flex-wrap gap-2`）」放在同一 flex 行（`EpicDetailPage.tsx:518`）。窄屏下当按钮组无法整组换行时，`justify-between` 仍把两者挤在同一行，按钮组的固有 min-content 宽度把 `flex-1` 的标题宽度压到接近 0，中文逐字竖排、页面异常拉长；动作按钮也会水平溢出被右边缘裁切。
- `packages/web/src/widgets/app-shell/ui/Header.tsx`：`usePageTitle()` 用 `useParams()` 取 `id` 再 `useEpic(params.id)` 解析编号（`Header.tsx:10,15,39-42`）。但 `Header` 在 `app/App.tsx:51` 作为 `<Routes>`（`App.tsx:53`）的**兄弟节点**渲染，落在匹配路由的 element 子树之外。React Router v7 的 `useParams()` 只返回路由上下文（`RouteContext`）里的动态参数，对 `<Routes>` 之外挂载的组件返回空对象 —— 因此 `params.id` 为 `undefined`，`useEpic('')` 无数据，`Epic #${epic?.number ?? ''}` 退化为裸 `Epic #`。issue 详情页没暴露这个问题，是因为它有路径分段兜底（`section.split('/')[2]`），epic 分支没有兜底。
- `packages/web/src/app/App.tsx:52` 内容容器为 `pb-14 md:pb-0`（56px），而 `MobileBottomNav`（`MobileBottomNav.tsx:72-74`）是 `h-14`（56px）`fixed bottom-0` 另加 `paddingBottom: env(safe-area-inset-bottom)`。在有 home indicator 的设备上导航栏实际高度 > 56px，超出预留空间，末尾 linked issue 被遮挡。

测试约束：`packages/web` 用 vitest + jsdom。jsdom 不做真实布局，`scrollWidth`/`clientWidth` 恒为 0，也无法应用 CSS media query。按项目约定（AGENTS.md：禁止真实外部系统、测试要快），像素级溢出测量不能在单测里可靠验证。

利益相关方：移动端浏览 Epic 的开发者（主用户）；Web 层唯一改动方；无 API/持久化消费者。

## Goals / Non-Goals

**Goals:**
- Epic 详情页在 320px / 390px / 430px 下对 `idle`/`running`/`paused`/`done`/`closed` 各状态无横向溢出。
- 详情头部移动端单列：标题与描述独占可读宽度，位于动作按钮组之上；长中文/长英文标题在可用宽度内换行，不被压成逐字竖排。
- 动作按钮在移动端全部可达、不被裁切；主动作（`Start Epic`/`Pause`/`Resume`）按状态保持直接可见，终端状态不出现生命周期动作。
- 顶部栏在 `/:projectName/epics/:id`（及 `:id` 段即编号的情形）显示真实 `Epic #<number>`，加载中显示 `Epic #…`，不再出现裸 `Epic #`。
- linked issues 区域在移动端不被固定底导航遮挡。

**Non-Goals:**
- 不重排详情页信息优先级（后续信息架构 issue 处理）。
- 不改 Epic lifecycle API / 状态机 / 动作-状态映射契约。
- 不改 linked issues 业务能力。
- 不引入端到端/真实浏览器像素级溢出测试（受 jsdom 限制，见 Risks）。
- 不重构 app shell 把 Header 移进路由树。

## Decisions

### D1. 详情头部移动端用响应式 flex-direction 强制单列
头部容器由 `flex flex-wrap items-start justify-between` 改为 `flex flex-col gap-4 md:flex-row md:flex-wrap md:items-start md:justify-between`。移动端：标题/描述块与动作按钮组上下堆叠；桌面端（`md:` 断点，与现有 `md:grid-cols-3` 等一致）恢复原单行布局，零回归。

标题块维持 `min-w-0`，并确保它独占整行宽度（移动端 `flex-1` 在 `flex-col` 下天然取满宽），保证 320px 下也有非零可读换行宽度。

- 备选 A：保留 `justify-between` 仅给标题加 `min-w-[200px]` —— 拒绝：无法覆盖所有标题/按钮组合，长标题仍会和按钮抢同一行。
- 备选 B：CSS Grid `grid-cols-1 md:grid-cols-[1fr_auto]` —— 可行，但为最小 diff 与现有 flex 风格一致，选 flex-direction。
- 备选 C：移动端按钮纵向堆叠（每个按钮全宽）—— 拒绝：过占垂直空间、滚动变长；横向 wrap 更紧凑。

### D2. 动作按钮组采用 flex-wrap，不引入「更多」下拉菜单
动作按钮组维持 `flex flex-wrap`，移动端设 `justify-start`（或 `justify-end`）让按钮在装不下时自然换行到下一行。主动作（`Start Epic`/`Pause`/`Resume`）按状态优先渲染、保持可见；次要动作（`Edit`/`Mark Done`/`Close Epic`）换行排布、每个都一次点击可达、不被裁切。纯 CSS、无新状态、保留现有 `data-testid`。

- 备选 A：次要动作收进 `DropdownMenu`（More ⋯）—— 拒绝：可见动作数 ≤5，换行已足够；下拉会增加组件、交互状态与 a11y 成本，且把次要动作藏到二次点击后。spec 明确允许「换行」方案。若未来动作数增长可再评估。

### D3. 全链路消除横向溢出（结构层面）
逐一确认页面中可能产生固有宽度的元素在移动端都能收缩/换行：
- 头部 → D1/D2 解决主要溢出源。
- 进度三宫格已是 `grid gap-4 md:grid-cols-3`（移动端单列），无需改。
- 加 issue 表单已是 `flex flex-col gap-3 sm:flex-row`（移动端堆叠），无需改。
- `LinkedIssueRow`（`EpicDetailPage.tsx:103`）右侧动作 `flex shrink-0 gap-2` 在 320px 下两个按钮可能偏紧：给该动作容器加 `flex-wrap`，让 `Start`/`Remove` 在极窄屏换行，避免把行撑出视口；标题侧 `min-w-0 flex-1` 已可收缩。
- 不引入任何 `min-w-[Npx]`/固定宽度。

### D4. Header 编号解析改为从 URL 路径派生 epic id（根因修复）
`Header` 在 `<Routes>` 之外挂载导致 `useParams()` 返回空。修复用 `useLocation()`（在 router 内任意位置都可用）从 `pathname` 解析出 `/:projectName/epics/:id` 的 `:id` 段，再交给 `useEpic()` 解析编号：

- 取路径分段，匹配 `.../epics/<seg>` 形态得到 epic id 段（兼容 `/epics/:id` 旧前缀路由与 `/:projectName/epics/:id` 项目前缀路由，统一现有「firstSegment 分支」与「section 分支」两条重复逻辑）。
- `useEpic(seg)` 加载中 → `Epic #…`；加载完成 → `Epic #<number>`；若 epic 无 `number` 字段，回退到稳定短标识（id 前缀），保证永不出现裸 `Epic #`。
- 路径分段即数字时（spec 场景 `:id` = `12`），`useEpic` 返回的 epic 数据 `number` 即为该值，自然显示 `Epic #12`。

- 备选 A：把 `Header` 移进匹配路由的 element 子树使 `useParams` 生效 —— 拒绝：Header 是跨路由共享的 app-shell chrome，侵入式重构、破坏 shell 布局。
- 备选 B：保留 `useParams` 仅加路径分段兜底（仿 issue 分支）—— 拒绝：仍依赖本就为空的 params，属治标；直接从路径派生 id 才是根因修复，且能顺手消除两条重复的 epic 解析分支。

### D5. 底部留白在 shell 容器层对齐真实导航栏高度（含 safe-area）
`MobileBottomNav` 是 shell 级 fixed 组件，其预留空间应由 shell 唯一负责。把 `app/App.tsx:52` 的 `pb-14 md:pb-0` 调整为 `pb-[calc(3.5rem+env(safe-area-inset-bottom))] md:pb-0`，使预留高度 ≥ 导航栏实际高度（含 home indicator inset），消除遮挡。

- 备选 A：在 `EpicDetailPage` 自身加底部 padding —— 拒绝：把 shell/导航关系散落到各页面，职责不清且需逐页复制。

## Risks / Trade-offs

- `[jsdom 无法测量 scrollWidth/clientWidth]` -> 单测改为断言**结构契约**（移动端单列 class、按钮组 wrap、标题在按钮组之前的 DOM 顺序、主动作按状态存在、终端状态无生命周期动作、shell 底部 padding class），把这些 class/结构视作 spec 中「无溢出」行为在 jsdom 下的等价可验证代理。spec 定义的 `documentElement.scrollWidth <= clientWidth` 作为像素级判据，留给真实浏览器手动/视觉回归核验（见 Open Questions），不在 CI 单测内执行。
- `[safe-area-inset 在无 home indicator 设备为 0]` -> calc 在普通设备退化为 3.5rem，与现状 `pb-14` 等价，无视觉回归。
- [`Header` 编号解析依赖 `useEpic` 解析能力] -> 若 epic id 段既非 id 也非可解析标识，`useEpic` 返回空，回退到短标识而非裸 `Epic #`；与现有详情页 `useEpic` 解析路径一致，不引入新失败面。
- [`Header` 现有单测在 `<Route>` 内挂载，掩盖了生产 mount 位置差异]` -> 新增/调整用例：除保留路由内挂载用例外，补一条**模拟生产 mount**（Header 在 `<Routes>` 之外、仅处 router 内）的用例，断言项目前缀路由下仍显示 `Epic #<number>`，防止回归。
- [`md:` 断点切换的桌面回归风险]` -> 桌面端（≥768px）类名恢复原 `flex-row`/`justify-between`，并跑现有详情页桌面用例；改动仅在移动端断点以下生效。

## Migration Plan

无数据/API 迁移，纯前端呈现改动。部署即生效，回滚即还原三个文件（`EpicDetailPage.tsx`、`Header.tsx`、`App.tsx`）。

验证步骤：
1. `npm run typecheck -w packages/web` 通过。
2. `npm run test:run -w packages/web` 通过，含新增移动端布局结构用例与 Header 编号解析用例。
3. 真实浏览器（DevTools 响应式 320/390/430）肉眼核验 running/idle/done/closed 四状态：无横向滚动、标题不逐字竖排、按钮不被裁切、底部不遮挡 linked issues、顶部栏显示真实 `Epic #<number>`。

## Open Questions

- 是否需要为 spec 的像素级 `scrollWidth <= clientWidth` 判据引入真实浏览器测试（如 Playwright）？当前判断不引入（受项目测试约定与范围限制），以结构契约单测 + 手动视觉核验覆盖。如团队希望自动化像素级校验，可作为后续独立 issue。
- `LinkedIssueRow` 在 320px 下「Start + Remove」换行是否影响可读性，需在真实浏览器核验后微调间距；当前决策是给动作容器 `flex-wrap` 作为安全兜底。
