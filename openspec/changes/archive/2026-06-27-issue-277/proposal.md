## Why

Epic 详情页在手机宽度（320–430px）下不可用：页面出现横向滚动，Running Epic 的标题/描述被 lifecycle 操作按钮组压到极窄宽度、中文逐字竖排，Idle Epic 的操作按钮在右侧被裁切、底部出现横向滚动条；同时顶部栏在项目名前缀路由下显示裸 `Epic #`（无实际编号），移动端上下文不完整。这是一次隔离的 Web 布局/页头修复，不涉及 API 或持久化。

## What Changes

- Epic 详情页头部在移动端改为单列：标题与描述独占可读宽度，不再与操作按钮组争抢同一行；长中文/长英文标题不会被压成逐字竖排。
- 操作按钮组在小屏下换行排布，主动作（`Start Epic` / `Pause` / `Resume`）保持可见，次要动作（`Edit`、`Mark Done`、`Close Epic`）有清晰入口且不被裁切。
- 消除 320px、390px、430px 视口下的横向溢出（`documentElement.scrollWidth <= clientWidth`），覆盖 running、idle、done/closed Epic 状态。
- 顶部栏在项目名前缀路由下显示 `Epic #<number>`，不再出现裸 `Epic #`。
- linked issues 区域与底部固定导航之间保留足够底部空间，避免遮挡关键内容。

## Capabilities

### New Capabilities

- `epic-detail-responsive-layout`: Epic 详情页（含顶部栏页头）在移动端宽度的响应式布局契约——无横向溢出、标题/描述与操作按钮分区、操作按钮按主/次可见性分级、页头显示真实 Epic 编号、linked issues 与底部导航之间留白。

### Modified Capabilities

（无。`epic-lifecycle` 中各状态露出哪些 lifecycle 动作的契约不变；本次只改呈现，不改动作-状态映射。）

## Impact

- **web**：`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx` 头部布局重构（标题/描述与动作按钮分区、小屏堆叠/溢出入口）、详情页容器底部留白；`packages/web/src/widgets/app-shell/ui/Header.tsx` 页头编号解析（项目名前缀路由下显示 `Epic #<number>`）。
- **测试**：`packages/web` 新增/扩展移动端宽度下的详情页布局关键状态用例（至少 running 与 idle Epic），以及 Header 编号解析用例。
- **API / 持久化 / 依赖**：无改动。
