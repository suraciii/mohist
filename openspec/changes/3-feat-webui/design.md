## Context

Mohist WebUI 是纯桌面优先设计的前端应用，位于 `packages/cli/web/src/`。使用 React + Tailwind CSS v4（CSS-based 配置，无 tailwind.config 文件），通过 Vite 构建。当前仅有 3 处响应式类（Header 的 `hidden sm:inline`、IssueDetailPage 的 `lg:grid-cols-3`），几乎所有组件使用硬编码的 `px-6`、固定宽度和 `h-[calc(100vh-4rem)]`。

关键路径：`packages/cli/web/` 下 `index.html`、`src/App.tsx`、`src/components/*.tsx`、`src/index.css`。

## Goals / Non-Goals

**Goals:**
- WebUI 在 <768px 视口上可舒适使用（查看 issue、审批、创建 issue、Explore 聊天）
- 纯 Tailwind 响应式实现，零新依赖
- 桌面端（>=768px）布局零变化

**Non-Goals:**
- PWA / Service Worker / 离线支持
- 手势操作（swipe、pinch）
- 深色模式
- 独立移动端路由或 URL 结构变更
- 后端 API 变更

## Decisions

### D1: 底部 Tab Bar 代替 Hamburger Menu

使用固定在底部的 3-Tab 导航栏（Board / Explore / Settings）代替传统的 hamburger menu。

**理由：** Mohist 只有 3-4 个主要页面，底部 Tab Bar 是移动端导航的标准模式（iOS Tab Bar / Android Bottom Navigation），一目了然、一步直达。Hamburger menu 增加操作步骤且隐藏导航选项。

**Alternatives considered:**
- Hamburger menu + 抽屉：导航选项被隐藏，用户需额外点击
- 顶部 Tab 栏：与 Header 争抢有限的顶部空间
- 精简 Header 按钮：4 个按钮即使缩小到图标也挤占 375px 宽度

**实现：** 新建 `MobileBottomNav.tsx`，`fixed bottom-0 inset-x-0 md:hidden`，高度 `h-14` + `pb-[env(safe-area-inset-bottom)]`。使用 `useLocation()` 高亮当前路由。在 `AppContent` 中渲染，与 `<Header />` 平级。

### D2: KanbanBoard 横向 Stage tabs + 单列视图

移动端将 5 列并排改为横向 scrollable tabs，选中 Stage 只显示该 Stage 的卡片列表。

**理由：** 5 列 x 280px min-width = 1400px，横向滚动体验差。单列 tab 切换是移动端看板的标准模式（Trello、GitHub Projects 均采用），内容聚焦且易于滑动。

**实现：** KanbanBoard 组件内根据 `window.innerWidth` 或 Tailwind 的 `md:` 断点切换两种渲染模式。移动端渲染 `<div className="md:hidden">` 区域（tabs + 单列卡片），桌面端渲染 `<div className="hidden md:block">` 区域（原有 flex 多列）。Stage tabs 使用 `overflow-x-auto snap-x snap-mandatory`。选中的 Stage 维护在组件 state（`useState`），默认选中第一个有 issue 的 Stage。

### D3: FAB 替代 Header 的 New Issue 按钮

在看板页面右下角显示浮动 "+" 按钮，点击打开 CreateIssueDialog。

**理由：** Header 移动端已隐藏 New Issue 按钮，需要一个替代入口。FAB 是移动端创建操作的标准模式（Material Design FAB）。

**实现：** 新建 `FAB.tsx`，`fixed bottom-20 right-4 md:hidden z-40`（bottom-20 = 80px，在 BottomNav 56px 上方留出空间）。仅在 `/` 路由渲染。点击触发与 Header 的 "New Issue" 按钮相同的 `setShowCreateIssue(true)` 逻辑。状态提升到 KanbanView 或 AppContent。

### D4: Dialog 移动端全屏

Dialog 组件在 <768px 时改为 `fixed inset-0` 全屏模式，隐藏 backdrop。

**理由：** 当前 Dialog 的 `max-w-lg`（512px）在移动端已经接近全宽，但缺少 `max-h` 和 `overflow-y-auto`，长表单内容会溢出。全屏模式简化了这个问题：内容自然滚动，不需要精确计算高度。

**实现：** 在 `Dialog.tsx` 的 overlay 容器上添加响应式类：移动端 `md:items-center md:justify-center`，dialog panel 移动端 `inset-0 md:relative md:max-w-lg md:rounded-lg`。dialog body 加 `overflow-y-auto`。仅修改 Dialog.tsx 基类，所有子类 Dialog（CreateIssueDialog、EditIssueDialog 等）自动继承。

### D5: 全局间距用 Tailwind 响应式替换

所有 `px-6` 改为 `px-4 md:px-6`，不做 CSS 变量或 JS 逻辑。

**理由：** 最小化实现复杂度。Tailwind 的 `px-4 md:px-6` 语义清晰，零运行时开销。涉及组件：IssueDetailPage、SettingsPage、LogsPage、ExplorePage。

### D6: viewport meta 和 safe-area 支持

在 `index.html` 添加 `viewport-fit=cover` 和 `theme-color` meta。

**理由：** `viewport-fit=cover` 是 `env(safe-area-inset-*)` 生效的前提。`theme-color` 让移动端浏览器地址栏与页面配色一致。

**实现：** 修改 `index.html` 的 viewport meta 为 `width=device-width, initial-scale=1.0, viewport-fit=cover`，添加 `<meta name="theme-color" content="#ffffff">`。

## Risks / Trade-offs

**[KanbanBoard 移动端/桌面端双渲染] → 使用 `md:hidden` / `hidden md:block` CSS 控制，共享数据获取逻辑和 IssueCard 组件，避免逻辑重复。** 风险：两套 DOM 同时存在但只显示一套。可通过条件渲染 `useMediaQuery` hook 避免，但需额外的 hook 实现。先用 CSS hidden 方案，如果性能有问题再改条件渲染。

**[Dialog 全屏模式影响所有 Dialog] → 逐个验证 CreateIssueDialog、EditIssueDialog、CreateProjectDialog、CustomProviderDialog、ProviderConnectDialog、DialogSelectDirectory。** 全屏模式对表单类 Dialog 是合适的，对选择类 Dialog（DialogSelectDirectory）可能过度。可在 Dialog 组件上增加 `fullscreen` prop 让调用方控制。

**[触摸目标 44px 需要逐个组件审查] → 优先处理高频操作按钮（审批、创建 Issue、导航 Tab），低频按钮作为 follow-up。** 全局搜索所有 `<button` 标签并添加 `min-h-[44px] min-w-[44px]` 是可行方案但工作量大。分两步：核心按钮在本次变更，其余按钮在 follow-up。

**[Logs 页面在移动端 BottomNav 中无直接入口] → 底部 Tab 只有 3 个位置（Board/Explore/Settings），Logs 不在 Tab 中。** 移动端用户通过 Settings 页面内的链接访问 Logs，或通过 URL 直接访问 `/logs`。桌面端 Header 的 Logs 链接不变。

## Migration Plan

无后端变更，纯前端 CSS/组件变更。部署步骤：

1. 修改 `index.html`（viewport meta、theme-color）
2. 创建新组件 `MobileBottomNav.tsx`、`FAB.tsx`
3. 修改 `App.tsx`（引入新组件、添加底部 padding）
4. 修改 `Header.tsx`（移动端隐藏按钮）
5. 修改 `KanbanBoard.tsx`（双模式渲染）
6. 修改 `Dialog.tsx`（全屏模式）
7. 修改各页面间距（`px-4 md:px-6`）
8. 修改 `index.css`（如需 safe-area 全局样式）

**验证：** Chrome DevTools 设备模拟器测试 375px（iPhone SE）、390px（iPhone 14）、768px（iPad Mini）。回归测试 >=1024px 桌面端布局无变化。

**回滚：** 所有变更为 Tailwind 类和组件级修改，git revert 即可。

## Open Questions

- Logs 页面移动端入口是否需要在 BottomNav 增加第 4 个 Tab？当前方案通过 Settings 页面内链接跳转，可能不够直观。可在实现阶段根据 Tab Bar 空间决定。
- ModelSelector 精简显示的具体截断策略（截断 provider 名？只显示模型名？）需实现时确认。
