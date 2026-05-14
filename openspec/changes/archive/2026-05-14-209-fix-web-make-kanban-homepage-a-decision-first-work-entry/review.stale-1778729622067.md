## Review

### Summary

The implementation addresses the core problems from the proposal: desktop Kanban layout is fixed from vertical (`flex-col`) to horizontal (`flex-row`), a `Needs attention` summary is added above the board, and the label filter is replaced from a hard `slice(0, 8)` to a searchable popover. However, the test suite has a critical hoisting failure that prevents all tests in the primary test file from running, and two spec requirements are only partially met.

### Errors (blocking)

#### E1: Test suite fails to run — `LABELS_MOCK` hoisting issue

**File:** `packages/cli/web/src/components/kanban-board-query.test.tsx:18-25`

`vi.mock()` is hoisted by Vitest above all `const` declarations. The mock factory on line 24 references `LABELS_MOCK` (declared on line 18), but at hoist time the variable is in the temporal dead zone. This causes `ReferenceError: Cannot access 'LABELS_MOCK' before initialization`, which prevents the entire test file from loading (0 tests run).

```
 Test Files  1 failed | 139 passed (140)
```

**Fix:** Use `vi.hoisted()` to define the constant so it is available when the mock factory executes:

```ts
const { LABELS_MOCK } = vi.hoisted(() => ({
  LABELS_MOCK: ['bug', 'feature', 'docs', 'workflow', 'ux', 'webui', 'improvement', 'reliability', 'session', 'agent'],
}))

vi.mock('../hooks/useQueries', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../hooks/useQueries')>()
  return {
    ...actual,
    useLabels: vi.fn().mockReturnValue({ data: LABELS_MOCK, isLoading: false }),
  }
})
```

Or inline the array directly in the mock factory.

#### E2: Duplicate attention items possible

**File:** `packages/cli/web/src/lib/homepage-attention.ts:13-60`

The function has five independent `if` blocks without early-return or deduplication. An issue can match multiple conditions and appear multiple times in the summary. Examples:

- `status === 'blocked'` + `mergeState === 'blocked'` → two entries (lines 32-38 and 41-49)
- `status === 'blocked'` + `stage === 'done'` + `mergeState === 'conflict'` → three entries (lines 32-38, skipped on 41-49 since mergeState is 'conflict', and 53-60)
- `stage === 'integrate'` + `mergeState === 'build-failed'` + `status === 'interrupted'` → two entries (lines 23-29 and 41-49)

**Fix:** Either use `else if` chains with a priority model (most specific match wins), or track seen issue IDs and skip duplicates:

```ts
const seen = new Set<string>()
for (const issue of issues) {
  if (seen.has(issue.id)) continue
  // ... match conditions, add seen.add(issue.id) on first match
}
```

### Warnings

#### W1: Done column de-emphasis not implemented

**File:** `packages/cli/web/src/components/StageColumn.tsx` (unchanged)

Spec REQ-WUI-209-002 scenario 3 requires: "its presentation is visually de-emphasized relative to active and attention work." The `StageColumn` component applies identical styling regardless of `isDone`. Consider adding muted colors (lower-contrast header, lighter card backgrounds) when `isDone` is true. The `isDone` prop is already threaded through but only affects the collapse/archive behavior, not visual weight.

#### W2: Mobile filter compaction incomplete

**File:** `packages/cli/web/src/components/KanbanBoard.tsx:78-193`

Spec REQ-WUI-209-002 scenario 2 requires: "filter controls are compact enough that issue content is visible in the first screen." The mobile `FilterBar` still renders all 5 priority buttons inline alongside the label popover trigger and search input. On a mobile viewport (375px), this still occupies significant vertical space. Design decision D5 specified collapsing priority, labels, and sort into a disclosure area on mobile, but no mobile-specific compaction was implemented.

#### W3: `_agentStatus` parameter unused

**File:** `packages/cli/web/src/lib/homepage-attention.ts:10`

The function accepts `AgentStatus` but the parameter is unused. Design decision D1 specifies the summary should derive from "existing issue and agent data." Currently all attention signals come from issue fields only.

#### W4: "Blocked" label vs "Needs action" per design

**File:** `packages/cli/web/src/lib/homepage-attention.ts:37`

The design doc explicitly calls out "Needs action" as the user-action label for blocked issues. The implementation uses "Blocked" instead. The spec uses "such as" language so this is flexible, but "Needs action" better matches the design's intent of avoiding raw internal state names.

### Complexity

- `KanbanBoard.tsx` is 416 lines. The `FilterBar` sub-component at ~162 lines and the `KanbanBoard` main function at ~100 lines are within reason. `NeedsAttentionSummary` is compact at 28 lines.
- `homepage-attention.ts` is 66 lines with a single exported function — clean and focused.
- No individual function exceeds 50 lines. Cyclomatic complexity is within limits.

### Security

No concerns. No user input is executed or injected. Label search is filtered client-side from pre-existing data.

### Spec Compliance

| Criterion | Verdict | Evidence |
|-----------|---------|----------|
| Desktop columns render horizontally at md+ | **PASS** | `KanbanBoard.tsx:390` — `flex-row` replaces `flex-col` |
| `Needs attention` summary above board | **PASS** | `KanbanBoard.tsx:325` — `<NeedsAttentionSummary>` renders before `<FilterBar>` |
| User-action labels (Approval needed, Integration failed, Interrupted, Not merged) | **PASS** | `homepage-attention.ts:18,24,48,57` — all four labels present |
| Selecting summary item opens issue | **PASS** | `KanbanBoard.tsx:244` — `<a href={/issue/${item.issueNumber}}>` |
| Board remains available below | **PASS** | Board renders after attention summary and filter bar |
| Label filtering reaches all labels | **PASS** | `KanbanBoard.tsx:44-48` — searchable popover with `allLabels` (no slice) |
| URL-backed board state preserved | **PASS** | Existing `serializeBoardQuery`/`parseBoardQuery` unchanged |
| Done column visually de-emphasized | **FAIL** | `StageColumn.tsx` unchanged; no muted styling for `isDone` |
| Mobile filter controls compact | **PARTIAL** | Label popover is more compact; priority buttons and sort still inline |
| Regression tests pass | **FAIL** | `kanban-board-query.test.tsx` fails to load (E1) |
| Tests cover desktop horizontal layout | **PASS** (code exists, doesn't run) | Lines 386-428 assert `flex-row` class and column count |
| Tests cover attention wording | **PASS** (code exists, doesn't run) | Lines 431-564 assert all attention labels |
| Tests cover hidden-label reachability | **PASS** (code exists, doesn't run) | Lines 567-673 test label search and selection |

<promise>FAIL</promise>
