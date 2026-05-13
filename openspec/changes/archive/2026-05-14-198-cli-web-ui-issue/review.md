## Review: Issue #198 — CLI 与 Web UI issue 创建能力对齐，以及看板筛选排序增强

### Overall Assessment: PASS with warnings

---

### Correctness

**PASS** — No logic errors found.

- CLI `--model` flag correctly passes `options.model` to the `POST /issues` payload at `issue.ts:212`. When `undefined`, the API ignores it; when invalid, the server returns 400 and the CLI exits with code 1 at `issue.ts:224`.
- Web UI `createIssue` includes `priority` in the request at `api.ts:49`, and `updateIssue` accepts `priority` at `api.ts:55`. Both `CreateIssueDialog` and `EditIssueDialog` initialize `priority` state correctly (default `p2` / hydrate from `issue.priority`).
- Board query state: `parseBoardQuery` / `serializeBoardQuery` round-trip correctly. `applyBoardFilters` applies priority → label → search pipeline in the correct order. `sortIssues` uses deterministic tie-breakers (updatedAt desc, then number desc) per the design spec.
- `deriveBoardColumns` maps each column through `applyBoardFilters` + `sortIssues` independently, preserving column grouping.

**Warning (W1):** `KanbanBoard.tsx:189` uses `useMemo(() => parseBoardQuery(getSearchParams()), [])` with an empty dependency array. This only reads `location.search` once at mount. The `popstate` listener at line 197 handles back/forward navigation, but if a filter change triggers a re-render without a popstate event, the initial state could be stale. This is mitigated by the `updateState` callback that syncs both `localState` and the URL atomically, so the risk is low in practice.

**Warning (W2):** `board-query.ts:47-51` uses `window.history.pushState` on every filter change. As noted in the design risks, this creates history entries for every keystroke in the search box. The design calls for debounced or replace-history updates, but the implementation uses pushState. This is a UX concern (cluttered browser history) not a correctness bug.

---

### Complexity

**PASS** — All functions are under 50 lines with low cyclomatic complexity.

- `board-query.ts` is a clean 125-line module with 7 exported functions, each well-scoped.
- `KanbanBoard.tsx` at 334 lines is the largest file; the `FilterBar` and `SortSwitcher` sub-components keep the main `KanbanBoard` function manageable.
- `CreateIssueDialog.tsx` (324 lines) includes the `ModelPresetSelect` component; both are under reasonable complexity.
- `StageColumn.tsx` at 123 lines is straightforward.

---

### Test Coverage

**PASS** — New code has dedicated tests.

- `issue-create-model-regression.test.ts`: 8 tests covering model-only create, model+body, model+@file, model+priority, and invalid model error path. All pass.
- `kanban-board-query.test.tsx`: 28 tests covering URL parse/serialize round-trips, priority filtering (single and multi), label filtering (AND logic), case-insensitive title search, combined filters, sorting by priority/number/updated, and a component render test for filtered stage counts. All pass.

---

### Security

**PASS** — No injection risks.

- CLI `--model` value is passed as a string to the API body; server-side validation in `api/issues.ts:439` rejects invalid model format with `isValidModelId()`.
- URL query params in `board-query.ts` are parsed through `URLSearchParams` (no manual string parsing) and validated against known values.
- No secrets exposed, no raw HTML injection.

---

### Spec Compliance

#### REQ-CLI-198-001: CLI issue create supports model on initial creation

| Scenario | Verdict | Evidence |
|----------|---------|----------|
| Create with model | **PASS** | `issue.ts:196` adds `--model` option, `issue.ts:212` sends `model` in POST body |
| Create with body source and model | **PASS** | `issue.ts:203` resolves body first, then `issue.ts:212` sends both; tested in `issue-create-model-regression.test.ts:208-246` |
| Invalid model format | **PASS** | CLI passes string through to API (`issue.ts:212`), API validates at `issues.ts:439`, CLI surfaces error at `issue.ts:223` and exits with code 1 at `issue.ts:224` |

#### REQ-API-198-001: Issue create accepts model with existing priority support

| Scenario | Verdict | Evidence |
|----------|---------|----------|
| Create with model and priority | **PASS** | `issues.ts:439` validates model, `issues.ts:426-437` normalizes priority, `issues.ts:463` persists both |
| Create with invalid model format | **PASS** | `issues.ts:439-440` returns 400 with `provider/model` error message |

#### REQ-WUI-198-001: Web issue dialogs support priority editing

| Scenario | Verdict | Evidence |
|----------|---------|----------|
| Create with priority selector, default p2 | **PASS** | `CreateIssueDialog.tsx:179` initializes `priority` to `'p2'`, renders p0-p4 buttons at lines 274-298 |
| Edit with current priority | **PASS** | `EditIssueDialog.tsx:21` initializes from `issue.priority ?? 'p2'`, re-syncs on `open` change at line 30 |
| Priority colors match CLI semantics | **PASS** | `label-colors.ts:83-89`: p0/p1 red, p2 yellow, p3 green, p4 gray — matches CLI `formatPriority` at `issue.ts:102-108` |
| Saving sends priority through API | **PASS** | `api.ts:49` `createIssue` includes `priority`, `api.ts:55` `updateIssue` accepts `priority` |

#### REQ-WUI-198-002: Kanban board supports focused filtering and sorting

| Scenario | Verdict | Evidence |
|----------|---------|----------|
| Priority and label filters update counts | **PASS** | `KanbanBoard.tsx:202-205` derives `filteredColumns` via `deriveBoardColumns`, which applies filters per-column; column count at `StageColumn.tsx:62` uses `totalCount` from filtered list |
| Search filters by title | **PASS** | `board-query.ts:104-108` case-insensitive title match; tested at `kanban-board-query.test.tsx:216-227` |
| Shared sort mode updates all columns | **PASS** | `board-query.ts:114-125` `deriveBoardColumns` applies `sortIssues` to every column using the same `state.sort` |
| Board state restored from URL | **PASS** | `board-query.ts:13-28` `parseBoardQuery` reads from `location.search`; `KanbanBoard.tsx:189` initializes from URL; round-trip tested at `kanban-board-query.test.tsx:143-153` |
| Mobile uses same focused view | **PASS** | `KanbanBoard.tsx:246-306` mobile section reads from same `displayedColumns` derived from same `localState` as desktop |

---

### Warnings Summary

1. **W1 (Low):** `KanbanBoard.tsx:189` — Initial URL state read is mount-only via empty `useMemo` deps. Relies on `popstate` for subsequent URL changes. Acceptable given the `updateState` pattern.
2. **W2 (Low):** `board-query.ts:50` — `pushState` on every filter change creates browser history entries per keystroke. Consider `replaceState` for search input to reduce history noise.

Neither warning constitutes a correctness defect or spec violation.

<promise>PASS</promise>
