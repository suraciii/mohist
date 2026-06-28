## Why

Epic 详情页当前是“文档详情页”：首屏先呈现完整 description，而用户打开一个 Epic 时最先想知道的是“它有没有在推进、当前 issue 是哪个、下一步为什么能或不能推进、我是否需要操作”。这些读侧判断事实（lifecycle、progress、advancement）后端已经算好（`EpicDetailDto`），但页面把它们排在长描述之后，导致状态判断被背景信息淹没。现在做是因为 Epic 自驱动（start/pause/resume/auto-advance）已落地，详情页必须从“文档页”升级为“目标状态工作台”才能匹配新的交互模型。

## What Changes

- Epic 详情页首屏在 description 之前展示 summary 区：progress（delivered/total、ready-to-done）、current activity（当前活跃/阻塞的 linked issue）、next issue / next issue reason。
- 长 description 降级为 Overview/Description 区域，放在 summary 之后，可折叠。
- 根据 lifecycle 状态突出单一主动作：`idle`→`Start Epic`、`running`→`Pause`、`paused`→`Resume`、`ready`（`readyToMarkDone`）→`Mark Done`。
- disabled `Mark Done` 在触屏上也显示可见原因（不依赖 hover tooltip），解释为什么不能完成。
- 各推进状态有清晰文案：running-but-idle、waiting for in-progress、draft blocker、external prerequisite blocker。
- Paused Epic 显示暂停原因，并说明 Resume 后会重新评估推进。
- Running Epic 的当前活动 issue 与等待原因支持直接跳转到相关 issue。
- Done/Closed Epic 不显示无效 lifecycle 主动作。
- 不引入任何后端/领域规则变化，不改自动推进选择规则。

## Capabilities

### New Capabilities

- `epic-detail-summary`: Epic 详情页的首屏信息架构契约——summary 区（progress、current activity、next issue/reason、ready-to-done）在 description 之前展示、description 降级为可折叠的次要区域、各推进状态（running-but-idle/waiting/draft blocker/external blocker/paused reason）的状态文案、以及从当前活动/等待原因到相关 issue 的跳转。

### Modified Capabilities

- `epic-lifecycle`: 「Epic detail page lifecycle actions」需求扩展——当 epic 处于 `ready`（`readyToMarkDone`）时 `Mark Done` 成为突出主动作；disabled `Mark Done` SHALL 显示触屏可见的原因，不依赖 hover/tooltip。

## Impact

- **web**：`packages/web/src/pages/epic-detail/` 详情页重构——新增 summary 区并前置、description 降级为可折叠 Overview、lifecycle 主动作按状态/readiness 突出、disabled `Mark Done` 可见原因展示、状态文案与 issue 跳转。
- **server / API / 持久化 / 依赖**：无改动。所有判断事实（`DeliveredCount`/`TotalIssueCount`/`ActiveIssues`/`BlockedIssues`/`NextIssue`/`NextIssueReason`/`ReadyToMarkDone`）已由现有 `EpicDetailDto` 提供。
- **测试**：`packages/web` 新增详情页 summary-first 信息架构、各推进状态文案与 lifecycle 主动作突出/disabled 原因可见性的用例。
