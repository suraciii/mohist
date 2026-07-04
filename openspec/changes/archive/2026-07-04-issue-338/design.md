## Context

Phase 1 (issue #336) shipped a readable ops-task execution log in the Web: the runner
captures stdout/stderr line-by-line, the server stores it on an independent channel, and
`TaskLogPanel` (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx`) renders each
`{ seq, timestamp, source, text }` line in a scrollable region with a truncation indicator.
Phase 2 added live SignalR delta append on top of the REST snapshot. The line model
deliberately carries **no `level`/`stream`** dimension — `source` is the phase label
(`workspace-prep`, `branch-check`, `action:rebase`, `cleanup`).

This issue closes the **consumption gap**: once a log runs long the user can only scroll
manually — no keyword search, no per-phase isolation, no export. All three needs are
answerable purely from the lines already in the React Query cache, so this is a
**client-only, single-file** change with no backend surface.

The reference interaction pattern is the system `LogsPage`
(`packages/web/src/pages/logs/ui/LogsPage.tsx`), which already ships a search input, a
`Set`-based chip multi-select, a `Blob` export, and scroll-aware auto-follow. This design
mirrors that proven pattern but does **not** share code with it (see D2), because the two
surfaces have different line models and different filter dimensions.

Constraints: `design/testing.md` forbids real external deps, wall-clock, and flakiness in
tests; a11y tests run under a separate vitest config excluded from default `npm test`.
Phase 1/2 rendering and live append must not regress.

## Goals / Non-Goals

**Goals:**

- Keyword search (case-insensitive substring over `text` + `source`) that narrows lines in
  real time as the user types, computed client-side via `useMemo`.
- Data-driven source-chip filters (one chip per distinct `source` in the loaded lines),
  multi-select, **the sole filter dimension** — there is intentionally no level/severity
  dimension because task-log lines have no `level`.
- Compositional filtering: a line shows only if it passes the search AND belongs to an
  enabled source.
- One-click download of the **currently filtered** view as a `.txt` via a client `Blob`.
- Replace the panel's blunt always-scroll-to-bottom with the LogsPage's scroll-aware
  auto-follow, so filtered/streaming views don't fight the user's scroll position.
- Distinct, actionable boundary states: empty log / no search match / no source match.
- Sensible defaults: empty search, all sources visible on open (and for sources that
  arrive later via live append).
- a11y: keyboard-reachable controls with accessible names, covered by a structural axe
  test under `packages/web/tests/a11y/`.
- Visual/interaction parity with the Logs page (search icon + placeholder, chip styling,
  export placement).

**Non-Goals:**

- No backend changes — no new endpoint, query param, or wire type. The REST snapshot, the
  SignalR delta channel, the `TaskLogLine` model, and the `WorkflowRun`/`WorkResult`
  domain are untouched.
- No search-result highlighting (filter only).
- No server-side full-text search.
- No level/error/warning severity dimension.
- No log sharing/external link (download is a local file).
- No extraction of a shared log-viewer component with `LogsPage` (see D2).

## Decisions

### D1. All logic inlined into `TaskLogPanel.tsx`; no new modules

The search `useState`, the `enabledSources` state, the compositional `useMemo` filter, the
`Blob` export, the scroll-aware auto-follow, and the boundary-state branches all live in
`TaskLogPanel.tsx`, alongside the existing Phase 1/2 subscription/merge logic.

**Rationale:** this is a single consumer of a single line model. Splitting filter/export
into a hook now would be premature — there is exactly one call site. The Phase 1 design
kept `mergeTaskLogDelta` co-located in this file for the same reason; this change follows
that precedent.

**Alternative considered:** extract a `useLogFilter(lines, { search, sources })` hook to
share with `LogsPage`. Rejected (see D2).

### D2. Mirror the LogsPage *pattern*, do not share code with it

`LogsPage` and `TaskLogPanel` share an interaction *shape* (search box + chip multi-select
+ `Blob` export + scroll-aware follow) but differ in substance:

| | LogsPage | TaskLogPanel |
|---|---|---|
| Line model | `ParsedLogEntry` (`level`, `service`, `raw`, `message`) | `TaskLogLine` (`seq`, `source`, `text`) |
| Chip dimension | fixed `LogLevel` enum (`ALL_LEVELS`) | **data-driven** distinct `source` set |
| Search haystack | `message + service + raw` | `text + source` |
| Export row | `entry.raw` | `line.text` |

A shared abstraction would need generic predicates and generic row accessors, and would
couple a Phase-3a UI tweak to the system LogsPage's future evolution. Visual parity is
achieved by reusing the **same Tailwind class strings** (chip shape, search-icon layout,
export-button placement), not by sharing logic.

**Rationale:** the similarity is a pattern, not a type. Premature DRY here would obscure
the domain-specific boundary states (empty/no-search/no-source) and the streaming-new-source
behavior (D4) that only the task panel has.

### D3. Filter dimension is `source` (phase), rendered with neutral chip styling

Chips are derived via `useMemo` from `Array.from(new Set(lines.map(l => l.source)))` — one
chip per distinct source present in the loaded lines, **never** a fixed enum. Source chips
use a **neutral slate palette**, deliberately *not* reusing `LEVEL_CHIP_COLORS`
(`packages/web/src/shared/lib/log-levels.ts`), because a source is a phase label, not a
severity — red/yellow/green chips would falsely imply error/warning/info.

**Rationale:** the issue's core domain point (carried from Phase 1's D3) is that filtering
is by phase origin, not by severity. Any level-colored chip would contradict the
"task-log has no level" invariant. Sort sources for deterministic render/test order
(stable sort by first appearance, or lexicographic — pick lexicographic for test stability).

**Alternative considered:** reuse `LEVEL_CHIP_COLORS` keyed by source string. Rejected — it
implies severity where none exists and breaks for sources not in the map.

### D4. `disabledSources: Set<string>` (opt-out) instead of `enabledSources` (opt-in)

Filter state is a set of **disabled** sources, not enabled ones. Empty set = all visible
(the default). Toggling a chip adds/removes its source from the disabled set; a line shows
iff `!disabledSources.has(line.source)`.

**Rationale:** sources are **data-driven and grow during live append** — a running task can
emit a `source` value that did not exist when the panel opened. With an opt-in
`enabledSources: Set`, a newly-arrived source would default to *hidden* (not yet in the
set), silently swallowing new phases mid-stream. The opt-out model keeps every new source
visible by default, which is the least-surprising behavior and matches "default = full log".
This is the one place the task panel genuinely diverges from LogsPage's
`enabledLevels` shape, and it is forced by the data-driven + streaming combination.

**Alternative considered:** mirror LogsPage's `enabledLevels: Set` and re-sync it whenever
the derived source set changes. Rejected — re-syncing risks flicker and the "did the user
toggle this or did it just arrive?" ambiguity; the opt-out set sidesteps both.

### D5. Compositional filter as a single `useMemo`

One `useMemo` over `[lines, disabledSources, searchQuery]` returns the visible lines:
`lines.filter(l => !disabledSources.has(l.source) && haystackIncludes(l, q))`, where the
haystack is `(l.text + ' ' + l.source).toLowerCase()` and `q = searchQuery.trim().toLowerCase()`.
Both the rendered list and the download consume this single derived array, guaranteeing
"download = what you see".

**Rationale:** AND semantics in one pass is the cheapest correct shape and makes
WYSIWYG-export automatic. The retained-tail cap (`TASK_LOG_RETAINED_LIMIT = 5000`) bounds
the per-keystroke cost to ≤5000 substring checks — trivial.

### D6. Download exports the **filtered** view; filename `task-logs-<taskId>-YYYY-MM-DD.txt`

`handleExport` maps the filtered `useMemo` array to `line.text` joined by `\n`, wraps it in
a `Blob({ type: 'text/plain' })`, and triggers a temporary `<a download>` exactly as
`LogsPage.handleExport` does (`createObjectURL` → append → click → remove → revoke). The
date segment is `new Date().toISOString().slice(0, 10)`. No debounce; click-driven only.

**Decision point (filtered vs full):** the spec requires the **filtered** view so export is
WYSIWYG — a user who narrows to `action:rebase` and downloads gets exactly those lines.
Exporting the full log would surprise users who filtered first. An empty filtered result
disables the button (matching LogsPage's `disabled={filtered.length === 0}`).

**Alternative considered:** export the full log regardless of filter. Rejected — violates
WYSIWYG and the spec's "download reflects the current filter" scenario.

### D7. Replace always-scroll-to-bottom with scroll-aware auto-follow (port from LogsPage)

The current `useEffect([data?.lines.length])` that force-sets `scrollTop = scrollHeight`
is replaced with the LogsPage pattern: a `userPausedAutoFollow` flag driven by a `scroll`
handler (pause when `distFromBottom > 10`, resume near bottom), and a follow `useEffect`
that scrolls only when not paused. This effect now depends on **`filtered.length`** (not
raw `lines.length`) so a filter change also respects stickiness.

**Rationale:** with filtering, the visible height changes independently of the data; the
old blunt scroll-on-`lines.length` would yank the viewport on every keystroke. The
LogsPage logic is proven and the spec mandates parity.

**Subtlety:** the follow effect keys on `filtered.length`; when the user narrows the
filter the viewport is intentionally *not* force-scrolled unless they are already pinned
to the bottom — which is the desired behavior.

### D8. Boundary states: deterministic priority

The scroll body renders one of four branches, evaluated in this order so each message is
unambiguous:

1. `isLoading` → "Loading execution log…"
2. `isError` → "Execution log unavailable"
3. `lines.length === 0` → empty-log message (`data-testid="task-log-empty"`, unchanged)
4. `filtered.length === 0` → split by cause:
   - `searchQuery` non-empty → "No lines match '<q>'"
   - else (some sources disabled) → "No lines match the active source filters"

If both a search term and disabled sources are active and the result is empty, the
**search** message wins (the user's most recent, most specific intent). Each branch keeps
its existing `data-testid` so Phase 1/2 non-regression assertions still hold.

### D9. a11y test under the separate a11y config

New `packages/web/tests/a11y/task-log-a11y.test.tsx`, mirroring
`settings-a11y.test.tsx`: mock `getIssueWorkflowTaskLog` (as the existing panel test does)
and the SignalR builder, render the panel with a multi-source fixture, and run the same
structural `axe` rule set plus a tab-order assertion over the search input, each source
chip, and the download button. Lives under the a11y vitest config that is excluded from
the default `npm test` (per `design/testing.md` — a11y is its own track).

**Rationale:** the search input needs a label (visible or `aria-label`), each chip is a
`<button>` whose accessible name is the source string, and the download button needs a
discernible name — all enforced by the structural rules already in the settings suite.

### D10. Functional test extension in the existing panel test file

Search/filter/export/boundary behavior is added to
`TaskLogPanel.test.tsx` (existing harness + fake SignalR + `buildHarness`). New cases:
typing narrows lines; toggling a source chip hides its lines; search + chip compose (AND);
download produces a `Blob`/filename matching the filtered set (assert via a
`vi.spyOn(URL, 'createObjectURL')` + anchor-click capture, or by spy on the export
builder); the three boundary messages render distinctly; default state shows everything;
scroll-aware follow does not force-scroll when paused. No new network mock is needed —
all assertions operate on cached `data.lines`, reinforcing the client-only requirement.

## Risks / Trade-offs

- **[Per-keystroke filter cost on a 5000-line tail]** → Mitigation: a single `useMemo`
  bounded by `TASK_LOG_RETAINED_LIMIT`; 5000 substring checks is sub-millisecond. No
  debounce needed.
- **[New source arrives mid-stream while user has toggled others off]** → Mitigation: the
  opt-out `disabledSources` set (D4) keeps new sources visible by default; only sources the
  user explicitly disabled stay hidden. Least-surprising for streaming.
- **[Filtered download surprises a user who expected the full log]** → Mitigation: this is
  the spec-mandated WYSIWYG behavior; the button is disabled when the filtered set is empty
  so a zero-file export cannot happen. Documented in D6.
- **[Blob URL leak if revoked before click resolves]** → Mitigation: follow LogsPage's exact
  append → click → remove → revoke order synchronously.
- **[Boundary message ambiguity when both search and source filters yield no rows]**
  → Mitigation: D8 pins a deterministic priority (search message wins); covered by a test.
- **[auto-follow regresses Phase 1/2 live append]** → Mitigation: the follow effect still
  fires on growth when the user is pinned to bottom; non-regression tests for live append
  pass unchanged. The only behavior change is *not* force-scrolling when the user has
  scrolled up — which is the intended new behavior.
- **[Shared-class drift between LogsPage and TaskLogPanel over time]** → Accepted trade-off
  (D2); visual parity is reviewed, not enforced by code sharing.

## Migration Plan

The change is **purely additive and client-only** — no server, runner, DB, wire type, or
endpoint changes. There is no data migration and no downtime.

1. Extend `TaskLogPanel.tsx` with search input, source-chip bar, download button,
   compositional `useMemo`, scroll-aware auto-follow, and boundary-state branches. Keep all
   existing Phase 1/2 subscription/merge/invalidation logic intact.
2. Extend `TaskLogPanel.test.tsx` with the new behavior + non-regression cases.
3. Add `tests/a11y/task-log-a11y.test.tsx` under the a11y config.
4. Verify: `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and the
   a11y suite. Confirm Phase 1/2 live-append tests still pass green.

**Rollback:** revert the single component file and the two test files. No other layer is
touched, so rollback is trivial and instantaneous with no data impact — the panel returns
to the Phase 1/2 scroll-only behavior.

## Open Questions

- **Source chip ordering.** Lexicographic vs first-appearance. Lean lexicographic for
  deterministic test output; confirm during implementation.
- **Search haystack scope.** Spec says `text` + `source`. Confirm whether the rendered
  timestamp should also be searchable (currently no — it is a presentation artifact, not
  content). Lean: exclude timestamp.
- **Export content richness.** Spec requires `line.text` per line. Confirm whether to
  prepend `[source]` and/or timestamp for context when pasting externally, or keep it
  bare-text to match LogsPage's `entry.raw`. Lean: bare `text` to match the spec wording
  exactly; richer format is a later enhancement.
- **a11y test scope.** Render the panel standalone (as the unit test does) vs. in its real
  `TaskProgressPanel` host. Lean standalone — the controls under test belong to the panel.
