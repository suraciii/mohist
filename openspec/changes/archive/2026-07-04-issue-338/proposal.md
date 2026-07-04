## Why

Phase 1 delivered a readable task execution log in the Web, but once a log runs
long (a full rebase spilling hundreds of lines) the user can only scroll
manually to find a specific error keyword, cannot isolate one phase's output
(e.g. only the `action:rebase` lines, filtering out `workspace-prep` noise), and
cannot export the log to paste into an issue comment or hand to a teammate.
Every search/filter/export concern is answerable purely from the lines Phase 1
already pulled to the client — no new server endpoint is warranted. This issue
closes that consumption gap so logs become navigable and shareable, reusing the
proven interaction pattern already shipped on the system Logs page.

## What Changes

- Add a **keyword search box** to the task log panel: a case-insensitive
  substring filter that hides non-matching lines in real time as the user types.
  Matching operates on each line's `text` (and its `source`), mirroring the
  system Logs page's `useMemo` substring approach.
- Add **source (phase) chip filters**: data-driven chips derived from the
  distinct `source` values present in the loaded lines (e.g.
  `workspace-prep`, `branch-check`, `action:rebase`, `cleanup`). Multi-select,
  all on by default, toggle to include/exclude. This is the only filter
  dimension — there is intentionally **no level/error/warning dimension**,
  because task-log lines have no `level` field (Phase 1 merged stdout/stderr and
  dropped level).
- Add a **download button** that exports the log as a `.txt` file via a client
  `Blob` (no server round-trip), using the same `createObjectURL` + temporary
  `<a download>` pattern as the system Logs page. Filename follows the existing
  convention (e.g. `task-logs-<taskId>-YYYY-MM-DD.txt`).
- Apply search + filter **composably**: a line is shown only if it matches the
  search term AND belongs to an enabled source chip.
- Port the system Logs page's **smarter auto-follow** (pause when the user
  scrolls away from the bottom, resume near bottom) so filtered/streaming views
  don't fight the user's scroll position — replacing the panel's current blunt
  always-scroll-to-bottom.
- Add friendly **empty/boundary states**: empty log, no search matches, and no
  source-chip matches each get a distinct, actionable message.
- Search/filter state has sensible defaults: search box empty, all source chips
  enabled (full log visible) on open.
- Visual and interaction parity with the system Logs page (search icon +
  placeholder, chip styling, export button placement) so the two log surfaces
  feel like one product.
- **No backend changes**: search, filter, and download all operate on the
  REST snapshot + live-appended delta already held in the React Query cache by
  Phase 1/2. No new endpoints, no new query params, no changed wire types.

## Capabilities

- `task-log-viewer`: Web consumption of a captured ops task's execution log.
  This change extends the existing Phase 1 capability (line-by-line rendering
  with source + timestamp, truncation indicator) with three client-only
  operations the user performs over the already-loaded lines: keyword search
  (case-insensitive substring), source/phase chip filtering (data-driven
  multi-select, the sole filter dimension — no level), and whole-log text
  download. It also adds the boundary states (empty, no search match, no filter
  match), sensible default filter state, scroll-aware auto-follow consistent
  with the system Logs page, and a11y for the new interactive controls. The
  Phase 1/2 data acquisition (REST endpoint, SignalR live append, `truncated`
  reporting) and the line data model (`{ seq, timestamp, source, text }`) are
  unchanged — this is strictly the viewing/interaction layer.

## Impact

- **Web (React)** — primary and only code surface:
  - `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx` gains the
    search input, source-chip filter bar, download button, compositional
    `useMemo` filter over `data.lines`, boundary states, and scroll-aware
    auto-follow.
  - Reuses the interaction primitives already proven on
    `packages/web/src/pages/logs/ui/LogsPage.tsx` (search `useMemo`, `Set`-based
    chip multi-select, `Blob` export). Chip styling may reuse
    `packages/web/src/shared/lib/log-levels.ts` where applicable, but the
    source set is **data-driven** (distinct `source` values from loaded lines),
    not a fixed enum like `LogLevel`.
- **Tests (web)**:
  - Extend `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx`
    (existing harness + fake SignalR) for search/filter/export behavior and
    boundary states.
  - Add an a11y test under `packages/web/tests/a11y/` (vitest-axe, separate
    config, excluded from default `npm test`) following
    `settings-a11y.test.tsx`, covering the new search input, chips, and
    download button.
- **No changes** to: server (C#), runner (TypeScript), the task-log REST
  endpoint, the SignalR delta channel, the `TaskLogLine` wire/type, the
  `WorkflowRun`/`WorkResult` domain, or any data store. Phase 1/2 log display
  and live append must not regress.
