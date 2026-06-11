## Why

Kanban 看板页面对 Cancelled issues 的可见性和交互存在多处 UX 断裂：术语在代码（`showClosed`）、UI 文本（"Show cancelled"）和测试（"Closed"）之间漂移；桌面端 CANCELLED 列标题默认可见但内容被 `filterClosedFromDone` 默认清空，需要去 Done 列旁边点一个外部按钮才能展开；移动端 Tab 计数在过滤后计算，导致 "Cancelled 0" 与实际 8 个 issues 不符；展开后没有收起路径；Cancelled 卡片没有状态标识。这次改动统一术语、把 toggle 收回到 CANCELLED 列内部、修正移动端计数、恢复双向切换、并在卡片上显示 Cancelled 状态 pill。

## What Changes

- 统一使用 **cancelled** 作为用户可见术语：状态值、变量、组件、测试断言、UI 文案全部对齐到 `IssueStatus.Cancelled`。
- 桌面端：CANCELLED 列内部提供 toggle 按钮，文本根据当前状态在 "Show cancelled" / "Hide cancelled" 之间切换，**不再** 把 toggle 放在 Done 列旁边。
- 桌面端：取消 `filterClosedFromDone` 的"清空列内容"副作用；CANCELLED 列始终渲染其全部 cancelled issues，列的"折叠"仅指 hide toggle 自身的内容（更准确地说：列卡片始终存在，toggle 决定列内是否显示 issues，列内空态与有内容态分别给用户清晰反馈）。
- 移动端：Cancelled tab 计数基于 issue 实际数量计算，不受 `showClosed` toggle 影响；移动端列表内提供等价的 toggle 控制显隐。
- `IssueCard` 取消 `indicator === 'cancelled'` 的隐藏逻辑：cancelled 卡片渲染灰色 Cancelled StatusPill。
- 调整 `kanban-board-query.test.tsx` 等相关测试断言，使其匹配新术语与新行为。

## Capabilities

### New Capabilities
无。

### Modified Capabilities
- `web-ui`: 调整 Kanban 看板对 Cancelled 列/卡片的渲染、计数与 toggle 行为；明确术语为 cancelled；在 `IssueCard` 上恢复 cancelled 状态 pill。

## Impact

- `packages/web/src/widgets/kanban-board/ui/KanbanBoard.tsx` — `showClosed` state 重命名（如 `showCancelled`），toggle 位置从 Done 列旁移至 CANCELLED 列内，文案改为 "Show cancelled" / "Hide cancelled"。
- `packages/web/src/widgets/kanban-board/model/kanban-grouping.ts` — `filterClosedFromDone` 与 `getDoneColumnCounts` 重新设计：列内容不再被默认清空，tab 计数基于 issue 真实数量。
- `packages/web/src/widgets/kanban-board/ui/IssueCard.tsx` — 取消 `indicator !== 'cancelled'` 排除条件。
- `packages/web/src/widgets/kanban-board/model/kanban-grouping.test.ts` 与 `packages/web/src/widgets/kanban-board/ui/kanban-board-query.test.tsx`（及周边测试）— 同步更新断言术语与行为。
- 不影响后端 API、不影响 `IssueStatus` 枚举语义、不影响 Backlog/InProgress/Done 列。
