## Context

The session list and discovery surface is fragmented across two parallel implementations and several dead stubs:

- **`SessionDetail` (`widgets/coder-session/ui/SessionDetail.tsx`)** renders only "Session info" — a dead stub.
- **`SessionList` (`widgets/coder-session/ui/SessionList.tsx`)** uses `useCoderSessions` + `SessionHeader` + `SessionDetail`. It is exported from the `coder-session` barrel (`index.ts:3`) but **no page or widget imports it** — confirmed by grep. Pure dead code.
- **`SessionHeader` (`widgets/coder-session/ui/SessionHeader.tsx`)** is consumed only by `SessionList` (and its own test). Also dead once `SessionList` is removed.
- **`WorkflowSessionsPanel` (`widgets/issue-workflow/ui/WorkflowSessionsPanel.tsx`)** is the canonical, actually-used list. It is rendered by `IssueDetailPage.tsx:926`. It renders `WorkflowSessionRow` in a two-line layout (name + status icon on line 1, metrics on line 2 with `flex-wrap`). It has **no filter controls, no sort controls**, and sorts unconditionally by `createdAt` ascending.
- **`SessionPage` (`pages/session/ui/SessionPage.tsx`)** has no sibling-session navigation (no prev/next, no sidebar). It fetches sessions via `useCoderSessions(issueNumber)` and the issue via `useIssue(issueNumber)` — the latter already provides `issue.workflowRunId`, giving access to `useWorkflowRunSessions`.

**Data sources:**

| Hook | Returns | Used by |
|------|---------|---------|
| `useCoderSessions(issueNumber)` | `CoderSessionSummary[]` | `SessionList` (dead), `SessionPage` (current-session lookup) |
| `useWorkflowRunSessions(workflowRunId)` | `WorkflowRunSession[]` | `WorkflowSessionsPanel` |

Both hooks are live-updating (SSE event subscriptions). `WorkflowRunSession` has all fields needed for filtering (status, model), sorting (createdAt, usage.totalTokens, completedAt/startedAt for duration), and display (sessionName, usage, eventSummary, failureReason).

**Constraints:**

- Filtering and sorting are client-side in-memory computations over data fetched by existing hooks.
- The workflow sessions API/read model is intentionally extended to include the list-discovery fields the frontend needs: stage, terminal status, `completedAt`, failure reason, and exit code. No runner, CLI, or persistence changes are required.
- The spec requires the sidebar session set to match the panel set for the same workflow run, and navigation (prev/next) to follow `createdAt` ascending.

## Goals / Non-Goals

**Goals:**

- Remove the dead `SessionDetail` stub, the unused `SessionList`, and their sole consumer `SessionHeader` — converging to a single canonical list implementation (`WorkflowSessionsPanel`).
- Add status and stage filtering to `WorkflowSessionsPanel`, combinable.
- Add sorting (createdAt asc default, tokens, duration) to `WorkflowSessionsPanel`.
- Ensure session rows wrap gracefully on narrow containers without horizontal overflow.
- Add prev/next sibling-session navigation to `SessionPage`.
- Add a sibling-sessions sidebar to `SessionPage` that matches the panel's session set.

**Non-Goals:**

- Full-text search of session content (out of scope per issue).
- Session export.
- Server-side pagination or server-side filtering (not needed; session counts per workflow run are small).
- Deleting or archiving sessions.
- Session-to-session diff/compare.
- Changes to the transcript reading experience (`agent-session-ui`, `session-transcript-navigation` specs unchanged).

## Decisions

### D1: Delete the entire dead `coder-session` list trio

Delete `SessionDetail.tsx`, `SessionList.tsx`, `SessionHeader.tsx` (+ their test files), and remove the `SessionList` export from `widgets/coder-session/index.ts:3`.

**Rationale:** Grep confirms zero external consumers. `getSessionLabel` / `getSessionStatusLabel` exports from `SessionHeader` are not imported anywhere else. `WorkflowSessionsPanel` already renders meaningful session info per row.

**Alternative considered:** Repurpose `SessionDetail` to show real data. Rejected — `WorkflowSessionsPanel`'s `WorkflowSessionRow` already shows the needed info; a second detail region would duplicate it.

### D2: Filter and sort logic lives in a custom hook within the `issue-workflow` widget

Create a `useWorkflowSessionFiltering` hook (in `widgets/issue-workflow/model/` or inline in the panel file if small) that owns:
- `statusFilter: string | null` and `stageFilter: string | null` state
- `sortKey: 'createdAt' | 'tokens' | 'duration'` state (default `'createdAt'`)
- A `useMemo` that filters then sorts `WorkflowRunSession[]` and returns the derived list

**Rationale:** Filtering/sorting are pure client-side view computations — they belong in the widget layer, not the entity layer (which is for data access). A hook keeps the logic unit-testable in isolation from rendering.

**Alternative considered:** Inline all state directly in `WorkflowSessionsPanel`. Rejected for testability — extracting the logic lets us test filter/sort combinations without rendering.

**Filter semantics:**
- Status options derived from what sessions surface: at minimum `running`, `completed`, `failed` (spec requirement). We enumerate all known statuses (`active`, `inactive`, `running`, `probing`, `completed`, `failed`, `cancelled`) and show only those present in the current session set, plus a "All" default.
- Stage options: `plan`, `build`, `check`, `integrate` (the executable pipeline stages, per spec).
- Filters combine with AND. Clearing one filter leaves others active.

**Sort semantics:**
- `createdAt` ascending is the default (preserves current behavior).
- `tokens`: by `usage.totalTokens` (falling back to `inputTokens + outputTokens` sum when total is absent, then 0).
- `duration`: computed as `completedAt - startedAt` (or `createdAt` if `startedAt` is null) for completed sessions; for live sessions, `now - startedAt/createdAt`. This matches the duration logic already present in `SessionPage.tsx:272-276` and `SessionHeader.tsx:96-101`.

### D3: Row layout — enforce `flex-wrap` and prevent overflow on `WorkflowSessionRow`

The current `WorkflowSessionRow` already has a two-line structure with `flex-wrap` on the metrics line. The main overflow risk is the model badge (`max-w-[180px] truncate`) and the session name on line 1.

Changes:
- Add `min-w-0` to the outer container and inner flex children to allow truncation.
- Ensure the name+status line wraps rather than overflows: the session name truncates with `truncate`, the model badge wraps to a second visual line on narrow widths.
- Verify no fixed-width elements force horizontal scroll.

**Rationale:** The two-line layout already mostly satisfies the spec; we tighten the CSS constraints rather than restructuring. The spec requires "name and status remain visible" and "no horizontal overflow" — both achievable with `min-w-0` + `flex-wrap` + `truncate`.

### D4: SessionPage uses `useWorkflowRunSessions` for sibling navigation

`SessionPage` already calls `useIssue(issueNumber)`, which provides `issue.workflowRunId`. Add a `useWorkflowRunSessions(issue?.workflowRunId)` call to obtain the canonical sibling session set.

**Prev/next navigation:**
- Sort siblings by `createdAt` ascending (canonical ordering per spec).
- Find the current session's index by matching `sessionName` (or `id` as fallback).
- Prev = index − 1, Next = index + 1. Disable/hide at boundaries.
- Navigation uses `react-router` `Link` to the sibling's transcript path (`/issues/:number/workflow/sessions/:sessionName`).

**Sidebar:**
- Renders the same `createdAt`-ascending sibling list as navigable `Link` entries.
- Highlights the current session (visual indicator).
- The set matches `WorkflowSessionsPanel`'s unfiltered set for the same workflow run — both read from `useWorkflowRunSessions(workflowRunId)`.

**Rationale for switching to `useWorkflowRunSessions`:** The spec requires the sidebar and panel to show the same set. Both must read from the same data source. `WorkflowRunSession` has all needed fields. The existing `useCoderSessions` call stays for current-session initial lookup (it's already wired and handles the route param matching).

**Alternative considered:** Derive siblings from `useCoderSessions` (issue-scoped). Rejected — `CoderSessionSummary` and `WorkflowRunSession` are different projections and the spec mandates parity with the panel, which uses `WorkflowRunSession`. Using the same hook guarantees the set matches.

### D5: Filter/sort UI controls in the panel header

Add a compact control row between the `CardHeader` summary and the session list:
- Status filter: a `<select>` (or button-group) with "All statuses" + present statuses.
- Stage filter: a `<select>` with "All stages" + plan/build/check/integrate.
- Sort: a `<select>` with Created / Tokens / Duration.

**Rationale:** Native `<select>` is the lightest accessible control and matches the app's existing minimal-control aesthetic. No need for a custom dropdown component.

**Alternative considered:** Inline chip-based filter toggles. Rejected — more visual noise in an already dense panel; selects are cleaner for mutually exclusive single-value filters.

### D6: Sidebar placement on SessionPage

The `SessionPage` currently fills its container vertically (header + scrollable transcript + composer). The sidebar will be rendered as a collapsible right-side panel on desktop (e.g. `xl:` breakpoint) and hidden or drawer-triggered on narrow viewports.

**Rationale:** The transcript view needs maximum width for readability. A sidebar that only appears on wide viewports avoids competing for space on mobile. On narrow viewports, prev/next buttons in the header provide the essential navigation.

## Risks / Trade-offs

- **[Client-side filter/sort is O(n) per render]** -> Mitigation: `useMemo` with proper deps; session counts per workflow run are typically < 20. Negligible cost.
- **[Live session duration drift]** -> The `duration` sort for running sessions depends on `Date.now()`, which changes every render. Re-evaluation only happens on re-render (triggered by SSE updates or user interaction), not on a fixed timer. This is acceptable — the panel already re-renders on SSE events. We do not add a polling timer solely for sort freshness.
- **[Two data sources on SessionPage]** -> SessionPage will call both `useCoderSessions` (legacy current-session lookup) and `useWorkflowRunSessions` (sibling navigation). This is a transitional redundancy. Risk: if the two sources diverge, the current-session highlight in the sidebar could mismatch. Mitigation: match by `sessionName` (the shared identifier used in routes); fall back gracefully.
- **[Sidebar hidden on narrow viewports]** -> Users on mobile lose the sidebar. Mitigation: prev/next buttons in the header are always visible, preserving the core navigation affordance.
- **[Deleting `SessionHeader` removes `formatDuration` helper]** -> That helper is duplicated in `SessionPage.tsx:78` and `WorkflowSessionsPanel` doesn't use it (it uses `relativeTime`). No shared dependency breaks.

## Migration Plan

This is a pure frontend change with no data migration or API changes.

**Deployment steps:**
1. Delete dead files (`SessionDetail.tsx`, `SessionList.tsx`, `SessionHeader.tsx`, their tests, barrel export).
2. Add filter/sort hook + controls to `WorkflowSessionsPanel`.
3. Tighten `WorkflowSessionRow` responsive layout.
4. Add prev/next navigation + sibling sidebar to `SessionPage`.
5. Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`.
6. Verify the panel renders correctly in the issue detail page and the session page sidebar/navigation works.

**Rollback:** Revert the web package changes. No server data is affected. The deleted files are recoverable via git.

## Open Questions

- Should the sidebar be collapsible by user preference (persisted), or always visible on wide viewports? **Default: always visible on `xl:`+; can add a toggle later if users report it as distracting.**
- Should stage filter options be limited to stages present in the current session set (like status), or always show all four (`plan`/`build`/`check`/`integrate`)? **Spec requires all four to be selectable. We show all four always.**
