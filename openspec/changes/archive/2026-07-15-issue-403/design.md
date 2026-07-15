# Design — Recoverable Files/Diff failure surface (issue-403)

## Context

The Files/Diff evidence page (`/issues/<number>/files`, `IssueChangedFilesPage.tsx`) is a dead end whenever evidence cannot be loaded. Today there are **two distinct failure branches**, and neither preserves issue context nor offers recovery:

1. **Transport/query error branch** — any of `useIssue`/`useIssueDiff`/`useIssueCommits` entering `isError` makes `useChangedFilesData.hasQueryError` true, and the page renders a page-local `ErrorState`: a red card with a single "View issue detail" link, no retry, and no issue title/health badge (`IssueChangedFilesPage.tsx:163-181`, branch at `:730-732`).
2. **Server-reported unavailability branch** — when the diff/commits response carries `available: false` with a reason (`runner_unavailable`, `workspace_removed`, `branch_missing`, `git_error`, `not_started`), `getDiffAvailability` derives an `unavailableMessage` and the page renders only a `PageHeader` + an orange banner (`DiffSummaryCard`, `:124-130`), with no retry and no link out (`:742-746`).

Both paths strand the user; reloading can leave them in the same state. The recovery gap is structural: `useChangedFilesData` (`:568-593`) returns boolean error flags and **discards the per-query `refetch` functions**, and the page has **no session awareness** at all (unlike `IssueDetailPage`, which resolves the active workflow-run session via `useWorkflowRunSessions`).

**Precedents already in the codebase:**
- **Retry pattern** — the Activity page captures each query's `refetch`, builds a `sourceErrors[]` array of `{ key, label, retry }`, and renders one inline retry control per failed source (`widgets/coder-session/model/activity-events.ts:525-572`, `pages/activity/ui/ActivityPage.tsx:249-266`).
- **Session link** — `IssueDetailPage` resolves the active session (`status` `active`/`running`/`probing`) from `useWorkflowRunSessions(issue.workflowRunId)` and links to `/issues/<number>/workflow/sessions/<sessionName>` (`pages/issue-detail/ui/IssueDetailPage.tsx:109-115,170-176`; route in `app/App.tsx:68`).
- **Design tokens** — recoverable banners elsewhere use `danger-subtle`/`warning-subtle` token classes; the Files page still uses legacy Tailwind utilities (`red-50`/`blue-600`).

This is a **web-only** change. Server/runner/CLI already expose the unavailability reasons and REST endpoints; no protocol or backend work is needed.

## Goals / Non-Goals

**Goals:**
- Replace the two dead-end failure branches with a **single, unified recoverable error surface** that converges both the transport-error path and the server-reported-unavailability path.
- Preserve issue context (number, title, health badge) on the surface — always the number; title + health when the issue itself loaded.
- Explain the failure in **product language**, translating each `ChangesUnavailableReason` (including the currently-unhandled `git_error`) and never surfacing raw reason identifiers or HTTP status as the user's primary guidance.
- Provide three recovery actions: **Retry** (re-fetch issue + diff + commits), **return-to-issue** (navigate to `/issues/<number>`), and a **related-session link** when an active workflow-run session is known.
- Update the existing recovery spec tests (which today lock in the dead-end behavior) to assert the new surface.

**Non-Goals:**
- No redesign of the Files/Diff viewer itself or of how diffs are computed.
- No durable retry queues, exponential backoff, or background recovery.
- No changes to `FullFilePane` per-file content fetch errors (`widgets/issue-changed-files/ui/FullFilePane.tsx`) — that is a *secondary* error inside an otherwise-loaded reader, not a page-level load failure. Candidate follow-up, explicitly deferred.
- No changes to inline sections `IssueDiffFilesSection` / `IssueCommitsSection` (they silently `return null` when unavailable) — out of scope; the acceptance criteria target the full page.
- No redesign of Activity or Coder Session pages; no shared-component extraction beyond the Files page.

## Decisions

### Decision 1 — Converge both failure paths onto one `RecoverySurface` component

Create a single page-local `RecoverySurface` component that both branches render. The spec is explicit that the two paths "SHALL NOT produce distinct dead-end states — both MUST converge on the same recoverable surface."

`RecoverySurface` takes a normalized failure cause plus the recovery affordances, so it does not care which path produced the failure:

```ts
type RecoveryCause =
  | { kind: 'transport' }
  | { kind: 'unavailable'; reason: ChangesUnavailableReason }
```

- **Alternatives considered:**
  - *Keep two components, share an actions sub-component.* Rejected — the spec mandates convergence, and a shared sub-component leaves two state-machine branches to keep in sync (the exact divergence that caused today's inconsistency).
  - *Extract a shared `RecoverySurface` into `shared/ui`.* Rejected as YAGNI; no other consumer exists yet and the Non-Goals forbid redesigning other pages. Keep it page-local; it can be promoted later if the inline sections or other evidence pages adopt it.

### Decision 2 — Restructure the state machine to derive a single `RecoveryCause`

Collapse the separate `ErrorState` and `!availability.diffAvailable` branches into one derivation evaluated in precedence order:

1. Invalid route param → `InvalidIssueState` (terminal, unchanged).
2. `data.hasQueryError` → cause `{ kind: 'transport' }`. (Definitive: a query in `isError` is not loading.)
3. Not loading **and** `diffData` present **and** `availability.unavailableMessage !== null` → cause `{ kind: 'unavailable'; reason }`.
4. `null` cause → either still loading, or the happy path.

If a cause is present, render `RecoverySurface`; otherwise fall through to loading / `ChangedFilesContent`.

**Why the unavailable cause is gated on "not loading":** `availability.diffAvailable` is `diffData?.available === true`, which is `false` while `diffData` is still undefined during initial load. Gating the unavailable branch on `!isLoading && diffData` prevents the recovery surface from flashing during the first load (this is the same hazard the current code avoids by ordering the loading check before the availability check).

### Decision 3 — Retry re-fetches all evidence sources (not per-source)

A single **Retry** action invokes `refetchIssue()`, `refetchDiff()`, and `refetchCommits()` together and lets the state machine re-evaluate. This differs from the Activity precedent (per-source retry) deliberately:

- The Activity page renders **independent zones** (events / agent activity / runners) that can partially fail and still be useful, so per-zone retry earns its complexity.
- The Files page renders **one unified evidence view** that is all-or-nothing: if any of issue/diff/commits fails, the view cannot render. Per-source retry therefore adds UI clutter without recovery value.

`useChangedFilesData` is changed to expose the three `refetch` functions (it currently drops them) and to surface `reason`/`unavailableMessage` so the cause and message can be derived. The spec's retry scenario ("the page MUST re-fetch the issue, diff, and commits sources") is satisfied exactly by re-fetching all three.

- **Alternative considered:** per-source retry mirroring Activity. Rejected for the all-or-nothing reason above. The per-source *error detection* is still captured internally to drive the message, but the control surface stays a single Retry.

### Decision 4 — Add session awareness via `useWorkflowRunSessions`

Call `useWorkflowRunSessions(issue?.workflowRunId)` in the page exactly as `IssueDetailPage` does, derive the first session with status `active`/`running`/`probing`, and render a session link to `/issues/<number>/workflow/sessions/<encodeURIComponent(sessionName)>` when one exists. The link is **absent** when there is no `workflowRunId` (e.g., the issue query itself failed) or no resolved session — matching the spec's absence scenario while keeping Retry and return-to-issue available.

The session query is independent of the evidence queries, so it resolves even when the diff is unavailable. Its cost is bounded: it is enabled only with a `workflowRunId` and carries `staleTime: 30s`.

### Decision 5 — Product-language message map, no raw identifiers

Map every `ChangesUnavailableReason` (including `git_error`, which `getDiffAvailability` currently drops into a generic fallback) to a human sentence, and never render the raw reason string or an HTTP status as guidance:

- `runner_unavailable` → "The file changes could not be loaded. The runner may be disconnected."
- `workspace_removed` → "The file changes could not be loaded. The workspace has been removed."
- `branch_missing` → "The file changes could not be loaded. The branch could not be found."
- `git_error` → "The file changes could not be loaded due to a git error."
- `not_started` → "There are no changes yet."
- `transport` (any query error) → "The file changes could not be loaded."

### Decision 6 — Context header is lightweight, not the full `PageHeader`

`RecoverySurface` renders a compact issue header (number, title when available, health badge) rather than the full `PageHeader` + `DiffSummaryCard`. The summary card's value (ahead/behind, branch names) is meaningless when evidence is unavailable, and the spec only requires number/title/health to remain visible. This also avoids coupling the recovery surface to `diffData`'s summary fields.

## Risks / Trade-offs

- `[Issue title absent when the issue query itself fails]` → Mitigation: the surface always renders the issue number (from the route param) and falls back to the transport message; title/health render only when `issue` is present. This satisfies the spec's "valid issue that has already been fetched" scenarios; the number-only case is the honest representation of an issue-load failure.
- `[Retry re-fetches healthy sources too]` → Acceptable trade-off for an all-or-nothing view; cheaper than per-source bookkeeping and matches the spec's "re-fetch issue, diff, and commits" outcome.
- `[Extra session fetch on the error path]` → Mitigation: `useWorkflowRunSessions` is gated on `workflowRunId` and has `staleTime: 30s`; it does not run when the issue failed to load (no id).
- `[Unified surface drops the current orange "Changes unavailable" banner]` → Intentional per spec; the same information now appears as product-language text on the unified surface, so no information is lost.
- `[Fixture router lacks the session route]` → Mitigation: the test harness will register a stub route for `/issues/:number/workflow/sessions/:sessionName` (as other tests do) so the session link can be asserted.

## Migration Plan

This is a web-only UI change with no backend, protocol, schema, or data migration.

- **Deploy:** single web PR; no feature flag required (the change replaces dead-end states with strictly-more-recoverable ones).
- **Server/runner/CLI:** no changes, no coordination needed — unavailability reasons and endpoints already exist.
- **Rollback:** revert the web commit; the old dead-end states return with no data consequences.

## Open Questions

- Should `FullFilePane`'s per-file fetch error (imperative `getFileContent`, no retry) be folded into a follow-up recovery pass? Currently deferred as a Non-Goal; tracked as a candidate follow-up issue.
- Should the inline `IssueDiffFilesSection` / `IssueCommitsSection` surface a lightweight recovery hint instead of silently returning `null`? Deferred — separate UX question from the page-level dead end.
- Promote `RecoverySurface` to `shared/ui` if a second consumer appears (e.g., the inline sections). Deferred until then to avoid speculative abstraction.
