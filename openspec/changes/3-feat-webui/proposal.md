## Why

WebUI 在手机浏览器（<768px）上基本不可用：Header 4 个导航按钮在 375px 宽度挤压换行，Kanban Board 多列布局溢出，按钮触摸目标过小（<44px），缺少移动端导航入口。随着 Mohist 进入日常使用，用户需要随时在手机上查看 issue 状态和审批。

## What Changes

- 新增 `MobileBottomNav` 底部 Tab 导航栏（Board / Explore / Settings），`md:hidden` 仅移动端显示
- 新增 `FAB` 浮动按钮组件，替代 Header 的 New Issue 按钮
- Header 移动端简化：隐藏所有导航按钮，只保留 logo + 项目选择器
- KanbanBoard 移动端改为横向 scrollable Stage tabs + 单列卡片视图
- IssueDetailPage / SettingsPage / LogsPage 间距调整为 `px-4 md:px-6`
- Dialog 移动端全屏模式
- 全局：按钮触摸目标 ≥ 44px、App 容器 `pb-14 md:pb-0`、viewport-fit=cover meta
- 不引入新依赖，纯 Tailwind 响应式实现

## Capabilities

### New Capabilities

- `mobile-layout`: 移动端响应式布局规范，包含底部 Tab 导航、Header 简化、FAB 浮动按钮、Kanban 单列模式、触摸目标尺寸要求、viewport meta 配置

### Modified Capabilities

- `web-ui`: Header 组件增加移动端隐藏导航按钮的行为，App 容器增加底部 padding
- `web-ui-logs-page`: Logs 页面间距和搜索框移动端适配

## Impact

- **新增组件**: `src/components/MobileBottomNav.tsx`, `src/components/FAB.tsx`
- **修改组件**: `Header.tsx`, `KanbanBoard.tsx`, `StageColumn.tsx`, `IssueDetailPage.tsx`, `ExplorePage.tsx`, `SettingsPage.tsx`, `LogsPage.tsx`, `Dialog.tsx`, `CreateIssueDialog.tsx`, `App.tsx`
- **全局**: `index.html`（viewport meta）, `src/index.css`
- **无后端变更，无 API 变更，无依赖变更**
