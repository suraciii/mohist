# Review Report

## Result: PASS

## Dimensions

### Correctness: PASS

- `groupIssuesByStage` correctly routes `status=Closed` to Done via display-only redirect. Backend `stage` field is never mutated.
- `filterClosedFromDone` returns the original array reference when `showClosed=true` — identity check avoids unnecessary re-renders.
- TypeScript types are correct. Typecheck passes with zero errors.
- `getDoneColumnCounts` safely handles missing Done column with `?? []`.
- Mobile tab bar uses `columns` (unfiltered) for counts — correct per spec requirement.
- Desktop `StageColumn` receives `displayCount` for Done column showing total count (including closed) while rendering filtered issues.
- `getBadgeType` priority ordering is correct: conflict → blocked → closed → approval → running. Each status maps to exactly one badge type.
- Overlay logic properly removed from IssueCard — no `isClosed` variable, no `opacity-50`, no `absolute inset-0` overlay div.

**Warning:** `APPROVAL_STAGES` constant at `IssueCard.tsx:8` is exported but never imported by any other file — dead code. Not an error but should be cleaned up.

### Complexity: PASS

- `kanban-grouping.ts`: 3 exported functions, all under 15 lines, cyclomatic complexity ≤ 3 per function.
- `KanbanBoard.tsx`: grouping logic cleanly extracted to `kanban-grouping` module — component is focused on UI rendering.
- `StageColumn.tsx`: `displayCount` prop is a minimal addition (1 line at line 36).
- `IssueCard.tsx`: badge logic is a simple priority chain in `getBadgeType` — clear and maintainable.
- No duplicated code.

### Test Coverage: PASS

- 11 tests covering all three exported functions in `kanban-grouping.ts`:
  - `groupIssuesByStage`: 5 tests (empty, active grouping, closed routing from multiple stages, non-closed unaffected, mixed)
  - `filterClosedFromDone`: 3 tests (show=true identity, show=false filtering, non-Done columns unaffected)
  - `getDoneColumnCounts`: 3 tests (empty, mixed counts, all completed)
- All 11 tests pass.
- IssueCard visual changes are UI-only (badge rendering, overlay removal) — no untested logic branches.
- Pre-existing failures (9 in `SettingsPage.test.tsx`) are unrelated — caused by missing Router context, not introduced by this change.

### Security: PASS

- Pure frontend UI changes. No external inputs, no injection risks, no secrets exposed.

### Spec Compliance: PASS

**T-001: IssueCard Blocked/Closed badge visual**

| Criterion | Result |
|-----------|--------|
| Blocked 状态显示红色/橙色 'Blocked' badge，无遮罩 | PASS — `IssueCard.tsx:63` renders `#ea580c` (orange) "Blocked" badge, no overlay |
| Closed 状态显示灰色 'Closed' badge，无遮罩 | PASS — `IssueCard.tsx:70` renders `bg-gray-200 text-gray-600` "Closed" badge, no overlay |
| 非 Blocked 非 Closed 状态不显示这两种 badge | PASS — `getBadgeType` returns `null` for Active/Paused/Interrupted/Completed |
| 其他 badge（Approval、Running、Conflict）按原有逻辑正常显示 | PASS — priority ordering preserved: conflict → blocked → closed → approval → running |
| Typecheck passes | PASS |

**T-002: KanbanBoard closed issue 归入 Done 列并增加 Show closed toggle**

| Criterion | Result |
|-----------|--------|
| status=Closed 的 issue 出现在 Done 列，不出现在原 stage 列 | PASS — `kanban-grouping.ts:22-23` routes Closed to Done; test confirms |
| Reopen 后 issue 回到原 stage 列 | PASS — display-only routing; `issue.stage` unchanged, natural fallback when status ≠ Closed |
| 非 Closed issue 按原有 stage 逻辑正常分组 | PASS — only Closed is intercepted; test confirms Blocked/Active stay in original stage |
| Show closed toggle 默认关闭，Done 列不显示 closed issue | PASS — `useState(false)` at `KanbanBoard.tsx:14` |
| 开启 toggle 后 Done 列显示 closed issue | PASS — `filterClosedFromDone` returns unfiltered columns when `showClosed=true` |
| Done 列中 Completed 等 non-Closed issue 不受 toggle 影响 | PASS — filter only removes `status === Closed`; test confirms Completed remains |
| Toggle 状态不持久化 | PASS — `useState(false)` with no localStorage/sessionStorage |
| 移动端 tab bar 和桌面端列头 count 包含 closed issue | PASS — mobile uses `columns` (unfiltered) at line 62/85; desktop uses `displayCount` override at line 112 |
| Typecheck passes | PASS |

## Fix Suggestions

1. `packages/cli/web/src/components/IssueCard.tsx:8` — Remove dead `APPROVAL_STAGES` export (imported nowhere). Low priority, no functional impact.
