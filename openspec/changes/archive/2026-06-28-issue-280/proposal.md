## Why

Epic 详情页的 Linked Issues 区域是桌面卡片网格的等比缩小版：每行把编号/标题/badges 与内联 `Start`、`Remove` 按钮挤在同一水平排（`EpicDetailPage.tsx:93-146`），手机上主阅读路径与破坏性动作争夺空间，`Remove` 一点即删、易误触；依赖图在窄屏上无可读降级。前置 issue 277 已修掉详情页整体横向溢出，但 Linked Issues 行与 Graph 视图仍需独立做移动端适配，让用户在手机上能扫视状态、安全地推进工作。

## What Changes

- Linked-issue 行改为移动优先的任务行布局：320–430px 下编号、标题、status/health、priority、start blocker 按优先级可读，无横向滚动。
- `Start` 仅在确实可启动且不违反单 in-progress 规则时出现（已由 `canInlineStartRow` 门控，保持不变）。
- `Remove` 移出主阅读路径，降级为 secondary/overflow 动作，并要求二次确认后执行 unlink，杜绝误触静默删除关联。
- Dependency Graph 视图在移动端明确降级：默认 List、提供可横向滚动的图，或显示"Graph works best on wider screens"提示，且 List 始终可达。
- Graph/List tab 切换在移动端保持可点击、可理解。
- 图不可渲染（cyclic / empty / 其它 unrenderable）时给出清楚说明，并保留 List 可继续使用。

## Capabilities

### New Capabilities

<!-- 无。所有改动都是 Epic 详情页既有的移动端呈现/交互区域的扩展，落到现有 responsive-layout 能力上。 -->

### Modified Capabilities

- `epic-detail-responsive-layout`: 扩展移动端契约——Linked-issue 行的可扫读布局（编号/标题/状态/优先级/阻塞原因优先）、`Remove` 动作的降级放置与二次确认、Graph 视图的移动端降级（默认 List / 可横滚图 / 窄屏提示 / 始终保留 List）、以及图不可渲染时的清楚说明与 List 兜底。

## Impact

- **web**：`packages/web/src/pages/epic-detail/ui/EpicDetailPage.tsx`（`LinkedIssueRow` 布局与动作、Graph/List 切换与 `showList` 兜底逻辑、窄屏降级提示）；`packages/web/src/widgets/epic-dependency-graph/`（移动端画布行为 / 降级文案）。
- **server / API / 持久化 / 依赖**：无改动。不引入新数据、不改 issue prerequisite 规则、不新增 graph layout 算法（详见 Non-Goals）。
- **测试**：`packages/web` 新增移动端 Linked-issue 行可读性、`Remove` 二次确认、Graph 兜底（cyclic / empty / 窄屏降级）的结构化用例。
