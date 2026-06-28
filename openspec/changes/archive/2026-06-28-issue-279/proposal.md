## Why

Epics 列表页把所有 `idle` 和 `running` 的 Epic 塞进同一个 `Active` 桶（`EpicListPage.tsx:223`），用户在一个长列表里无法快速判断哪个 Epic 真的在跑、哪个可以启动、哪个在等待、哪个是 idle-empty。列表页应当服务扫视和决策，而当前分组抹平了优先级差异。现在做是因为 Epic 自驱动（start/pause/resume/auto-advance、running-but-idle 可观测性）已落地，列表页必须按"需要先关注什么"重新分组才能匹配新的交互模型。

## What Changes

- 把当前的 `Active` 桶拆分为基于已有事实（lifecycle status + next issue + next issue reason + active/blocked linked issue + readyToMarkDone）的呈现分组，不改任何领域状态：
  - **Running**：有 in-progress linked issue 的 Epic 独立分组，置于列表最上方，并在卡片上展示 current issue / waiting reason。
  - **Ready to start**：有非空 `nextIssue`（server 已挑出可启动的 next issue）的 Epic 与 running/waiting 分开。`CanStart` 只存在于 epic 详情的 `LinkedIssue` 上，列表 read model 不提供，故分组以 `nextIssue` 是否为空为准。
  - **Waiting / Blocked**：next issue 不可启动且原因明确（draft、external prerequisite、waiting for in-progress slot）的 Epic 分开。
  - **Idle / Empty**：无 startable issue 或无 linked issue 的 Epic 单独分组，展示清晰原因。
- 卡片上的手动 `Start` 文案澄清为 `Start next issue`，与 `Start Epic`（Epic lifecycle 转换）明确区分，并降低误触风险（位置/视觉层级不与卡片主导航冲突）。
- 列表页移动端保持可读：不产生横向滚动，卡片关键状态（status / current issue / next / reason）不被截断到不可理解。
- **Done** 和 **Closed** 继续默认折叠的保留行为不变。
- 不引入后端 / 领域规则变化，不改 Epic 自动推进选择规则，不改列表查询性能。

## Capabilities

### New Capabilities

- `epic-list-presentation`: Epic 列表页的呈现契约——基于已有 Epic read model 事实（lifecycle status、next issue / next issue reason、active/blocked linked issue、readyToMarkDone）的状态分组（running / ready-to-start / waiting-blocked / idle-empty）与排序、卡片状态展示（current issue / waiting reason）、手动 `Start next issue` 与 `Start Epic` 的语义区分、以及移动端可读性（无横向滚动、关键状态不截断）。

### Modified Capabilities

<!-- 无。分组是呈现策略，不改 epic-lifecycle 状态机，也不改 epic-list-query 的后端查询/正确性契约；所需判断事实已由现有 read model 提供。 -->

## Impact

- **web**：`packages/web/src/pages/epics/ui/EpicListPage.tsx` 列表分组重构——拆分 `Active` 桶为 running / ready-to-start / waiting-blocked / idle-empty、Running 分组置顶、卡片状态展示与 `Start next issue` 文案澄清、移动端可读性。
- **server / API / 持久化 / 依赖**：无改动。分组所需事实（`Status`/`NextIssue`/`NextIssueReason`/`ActiveIssues`/`ReadyToMarkDone`）已由现有 `EpicWithProgress` 提供。
- **测试**：`packages/web` 新增列表页分组（running / ready-to-start / waiting reason / done-closed folded）与 `Start next issue` 语义澄清的用例。
