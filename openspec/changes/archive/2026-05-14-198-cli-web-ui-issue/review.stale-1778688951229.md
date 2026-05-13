## Review

### Summary

The implementation delivers CLI `--model` parity and Web UI priority controls well, but the Kanban board filter/sort feature has a critical state wiring bug that makes it non-functional. Two integration tests also fail due to missing router context.

---

### Errors

#### E1: Kanban board filtering/sorting is non-functional (state wiring bug)

**File:** `packages/cli/web/src/components/KanbanBoard.tsx:189-196`

`filteredColumns` (and the entire displayed board) is derived from `queryState`, which is memoized on `[issues]`:

```tsx
// line 189: only recalculated when issues change
const queryState = useMemo(() => parseBoardQuery(getSearchParams()), [issues])

// line 193-196: board display uses queryState
const filteredColumns = useMemo(
    () => deriveBoardColumns(allColumns, queryState),
    [allColumns, queryState],
)
```

When the user interacts with FilterBar, `updateState` (line 215) updates `localState` and pushes to the URL, but `queryState` is **not recalculated** because `issues` didn't change. The displayed board stays unfiltered regardless of user input.

**Fix:** Derive `filteredColumns` from `localState` instead of `queryState`. `queryState` should only be used for initial hydration:

```tsx
// line 189: keep for initial hydration only
const queryState = useMemo(() => parseBoardQuery(getSearchParams()), [])

// line 209: initialize from queryState (already correct)
const [localState, setLocalState] = useState<BoardQueryState>(queryState)

// line 193-196: use localState instead of queryState
const filteredColumns = useMemo(
    () => deriveBoardColumns(allColumns, localState),
    [allColumns, localState],
)
```

**Spec impact:** REQ-WUI-198-002 scenarios "Priority and label filters update board counts", "Search filters by title", "Shared sort mode updates all columns", and "Mobile board uses the same focused view" all **FAIL** because the board never reflects user filter/sort changes.

#### E2: Kanban board ignores browser back/forward navigation

**File:** `packages/cli/web/src/components/KanbanBoard.tsx:215-220`

`updateState` uses `window.history.pushState` to update the URL, but there is no `popstate` event listener. When users press browser back/forward, the URL changes but neither `localState` nor `queryState` updates.

**Fix:** Add a `popstate` listener that re-parses URL into `localState`:

```tsx
useEffect(() => {
  const handler = () => {
    setLocalState(parseBoardQuery(getSearchParams()))
  }
  window.addEventListener('popstate', handler)
  return () => window.removeEventListener('popstate', handler)
}, [])
```

#### E3: Two KanbanBoard integration tests fail

**File:** `packages/cli/web/src/components/kanban-board-query.test.tsx:317, 337`

Both `renders all columns with unfiltered issues` and `displays filtered issue count after priority filter applied` fail with:

```
TypeError: Cannot destructure property 'basename' of 'React10.useContext(...)' as it is null.
```

`KanbanBoard` renders `IssueCard` which uses react-router's `<Link>`. The test wraps the component in `QueryClientProvider` but not in `BrowserRouter`.

**Fix:** Wrap render in `<MemoryRouter>`:

```tsx
import { MemoryRouter } from 'react-router-dom'

render(
  <QueryClientProvider client={queryClient}>
    <MemoryRouter>
      <KanbanBoard issues={issues} agentStatus={mockAgentStatus} />
    </MemoryRouter>
  </QueryClientProvider>,
)
```

---

### Warnings

#### W1: Priority selector ring color has no visual effect

**Files:** `CreateIssueDialog.tsx:290`, `EditIssueDialog.tsx:124`, `KanbanBoard.tsx:88`

The selected priority button sets `{ ringColor: style.text }` via inline `style` prop. `ringColor` is a Tailwind CSS utility, not a valid CSS property — it has no effect when set via inline styles. The ring always renders in the Tailwind default color.

**Fix:** Use `--tw-ring-color` CSS variable instead:

```tsx
style={{
  backgroundColor: style.bg,
  color: style.text,
  ...(priority === p ? { '--tw-ring-color': style.text } as React.CSSProperties : {}),
}}
```

Or use a conditional Tailwind class with a custom ring color per priority.

#### W2: `updateBoardURL` in board-query.ts is dead code

**File:** `packages/cli/web/src/lib/board-query.ts:47-51`

`updateBoardURL` is exported but never imported or called. The URL update logic is inlined in `KanbanBoard.tsx:217-219`. Remove the unused function or refactor `KanbanBoard` to use it.

#### W3: Label filter capped at 8 labels

**File:** `packages/cli/web/src/components/KanbanBoard.tsx:109`

`allLabels.slice(0, 8)` prevents users from selecting labels beyond the first 8. Consider a collapsible list or a scrollable dropdown if the project has many labels.

---

### Correctness

- **CLI `--model` passthrough (T-001):** Correct. `issue.ts:196` adds the option, `issue.ts:212` includes it in the POST payload. Server-side validation via `isValidModelId` at `api/issues.ts:439` handles invalid format. Exit code 1 on error confirmed at `issue.ts:224`. No client-side model validation — follows design decision D1.
- **Priority controls in dialogs (T-002):** Correct logic. `CreateIssueDialog` defaults to `p2` (line 179), sends `priority` in mutation (line 192). `EditIssueDialog` initializes from `issue.priority ?? 'p2'` (line 21), sends in update mutation (line 43). Both use `getPriorityStyle` from shared `label-colors.ts`.
- **API types (T-002):** `api.ts:49` includes `priority` in `createIssue` params. `api.ts:55` includes `priority` in `updateIssue` params. Types align with backend expectations.
- **Board query state (T-003):** `board-query.ts` pure functions are correct: parse/serialize round-trip, filter by priority/label/search, sort by priority/number/updated with tie-breakers. Missing priority normalization to `p2` is correct. All 26 pure unit tests pass.
- **Board filter/sort UI (T-004):** `FilterBar`, `SortSwitcher` components are well-structured. Desktop and mobile both consume same `displayedColumns`. Sort is global (shared across columns). **But see E1** — the state wiring is broken.

### Complexity

All functions are under 50 lines. `FilterBar` (~110 lines) and `KanbanBoard` (~150 lines) are the largest components — acceptable. `sortIssues` has cyclomatic complexity ~6. `board-query.ts` is a clean, testable module. Good separation of concerns.

### Test Coverage

| Area | Tests | Status |
|------|-------|--------|
| CLI create with --model (body combos, invalid) | 5 tests in `issue-create-model-regression.test.ts` | PASS |
| Board query parsing/serialization | 10 tests | PASS |
| Board filtering (priority, label, search, combined) | 8 tests | PASS |
| Board sorting (priority, number, updated) | 3 tests | PASS |
| Board URL round-trip | 1 test | PASS |
| KanbanBoard component rendering | 2 tests | FAIL (E3) |
| EditIssueDialog priority | 0 tests | Missing |
| CreateIssueDialog priority | 0 tests | Missing |

### Security

No injection risks. Model validation happens server-side. Priority validated via `normalizePriority`. URL query params parsed safely with `URLSearchParams`. No secrets exposed.

---

### Spec Compliance

| Requirement | Verdict | Evidence |
|-------------|---------|----------|
| REQ-CLI-198-001: CLI create with model | **PASS** | `issue.ts:196` adds `--model`, `issue.ts:212` sends in POST. Tests confirm. |
| REQ-CLI-198-001: Body + model combined | **PASS** | `issue.ts:203-212` resolves body first, model in same request. Test at line 208. |
| REQ-CLI-198-001: Invalid model error | **PASS** | API returns 400, CLI surfaces error + exit 1. Test at line 249. |
| REQ-API-198-001: Create with model+priority | **PASS** | `api/issues.ts:416` reads both, validates model at :439, persists at :463. |
| REQ-API-198-001: Invalid model 400 | **PASS** | `api/issues.ts:439-440` returns 400 with explanation. |
| REQ-WUI-198-001: Create dialog priority selector | **PASS** | `CreateIssueDialog.tsx:179` defaults p2, renders p0-p4 at :272-298. |
| REQ-WUI-198-001: Edit dialog priority | **PASS** | `EditIssueDialog.tsx:21` loads current priority, selector at :106-132. |
| REQ-WUI-198-001: Priority color semantics | **PASS with warning** | Colors correct in `label-colors.ts:83-89`, but ring highlight broken (W1). |
| REQ-WUI-198-002: Filters update board counts | **FAIL** | E1 — board display derived from `queryState` which doesn't update on user interaction. |
| REQ-WUI-198-002: Search filters by title | **FAIL** | Same root cause E1. |
| REQ-WUI-198-002: Shared sort updates all columns | **FAIL** | Same root cause E1. |
| REQ-WUI-198-002: Board state restored from URL | **PASS** | Initial load parses URL correctly. Round-trip test confirms. |
| REQ-WUI-198-002: Mobile uses same filtered view | **FAIL (if fixed)** | Mobile uses same data pipeline, will work once E1 is fixed. |

<promise>FAIL</promise>
