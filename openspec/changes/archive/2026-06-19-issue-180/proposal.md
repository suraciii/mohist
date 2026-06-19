## Why

打开一个 issue 详情页，第一眼该看到的是「现在什么状态、要不要我处理」——这些已被 `RuntimeDecisionSurface` 在首屏讲清。但 Activity 事件流却铺在主内容区，和 Description、Comments、下一步动作抢空间，用户要滚很久；同时首屏多个彩色大块叠色、模块零间距，密度过高。需要把信息层级修正回来：该突出的升到首屏，诊断性的事件收进按需弹窗，并压低首屏彩色噪声与密度。

## What Changes

- 从 `IssueDetailPage` 主内容区移除 `EventTimelinePanel`；主区默认零 Activity 痕迹（连「最近失败」提示都不留——blocked 原因已由 `RuntimeDecisionSurface` 在首屏讲清）
- 在标题区右侧新增 `Activity` 入口按钮，点击以 Dialog 形式打开完整事件时间线，复用项目既有 Dialog 惯例（`WorkflowYamlDialog` / `ReviewReportModal`）
- 事件历史改为懒加载：仅在弹窗打开时拉取 `GET .../events`，页面初始加载不拉取事件
- 弹窗内复用现有分类筛选、新旧排序、事件详情展开能力
- `Activity` 入口首次打开前不显示精确事件计数（避免为计数预加载全部事件）
- 关闭弹窗期间发生的事件不丢失：再次打开通过持久化历史完整呈现
- 弹窗内视觉降权：常规事件（workflow/approval/integration/success/metadata）回归中性单色，去掉分类饱和色、分类徽章、整行铺彩底；仅 failure/attention 保留彩色强调
- 事件详情展开块从深色 `bg-gray-900` 收回中性浅底；live 事件自动入场动画收敛或去除
- `RuntimeDecisionSurface` 从整片彩色背景改为白底 + 左侧彩色边条，保留状态文案与动作按钮；各运行状态（running/queued/approval-required/blocked/failed/done）改版后仍视觉可区分
- 统一间距阶梯：相关元素成组紧凑、组间用留白拉开，用间距分组替代装饰性边框；Tasks/Checks 列表项成组紧、组间留白；首屏「下一步动作」区给足缓冲
- 移动端：Activity 弹窗以近全屏 sheet 呈现（非居中小框），入口按钮可见可达；被改到的组件做移动端适配，无功能丢失

## Capabilities

### New Capabilities

- `issue-detail-activity-dialog`: Issue 详情页上以按需弹窗访问 Activity 事件时间线的交互模式——标题区入口按钮、打开时懒加载事件历史、关闭期间事件不丢失（重开即完整呈现）、首次打开前不显示精确计数、移动端近全屏 sheet、主内容区默认零 Activity 痕迹。弹窗内复用既有的筛选/排序/详情展开能力。

### Modified Capabilities

- `issue-event-timeline`: 事件加载时机从「页面打开时」改为「Activity 弹窗打开时」；常规事件分类回归中性单色（去掉分类饱和色、分类徽章、整行铺彩底），仅 failure/attention 保留彩色强调；事件详情展开块由中性深底改为中性浅底；live 入场动画收敛。
- `issue-runtime-decision-surface`: 视觉处理从整片彩色背景改为白底 + 左侧彩色边条，保留状态文案与动作按钮；各运行状态在不依赖整片彩色背景的前提下仍视觉可区分。
- `web-ui`: 移除「Issue 详情页主内容列渲染 Activity 面板」的既有要求（改为弹窗入口）；新增 Issue 详情页密度与节奏要求——统一间距阶梯、成组紧凑 + 组间留白替代装饰性边框、列表项（Tasks/Checks/事件行）成组、首屏下一步动作区缓冲。

## Impact

- **前端组件**: `IssueDetailPage`（移除 `EventTimelinePanel`、新增 Activity 入口与 Dialog、密度调整）、新增 Activity Dialog 组件、`EventTimelinePanel`（颜色降权、加载时机、详情块底色、动画）、`RuntimeDecisionSurface`（背景/边条改版）
- **数据获取**: 事件 `GET .../events` 调用从页面初始加载推迟到弹窗打开；不新增独立事件计数接口（计数不在首屏展示）
- **既有契约**: 无 API/CLI/schema 契约变更；事件端点、筛选/排序/详情展开行为保留
- **测试**: `EventTimelinePanel.test.tsx`、`RuntimeDecisionSurface.test.tsx`、`IssueDetailPage.test.tsx` 需更新（加载时机、颜色降权、弹窗入口、移动端 sheet、密度回归）
- **风险**: medium——改动集中在单个 Web 页面，但横跨多个组件并改变了「如何访问 Activity」这一主要用户流程，UX 影响面较大
