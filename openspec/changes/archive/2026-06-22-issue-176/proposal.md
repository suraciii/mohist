## Why

The epic detail page lists linked issues as homogeneous rows (`active / Backlog / P0`), so a user cannot see at a glance which issue depends on which, what can be started right now, and what is still waiting on a prerequisite to deliver. The dependency structure already lives in the domain (`Issue.PrerequisiteNumbers`, derived `CanStart` / `Blocker`), but it is never rendered as a graph. With epic-scoped issue counts small (typically <20), a client-side DAG visualization turns the list into a decision-support surface: ready issues, waiting issues, and in-flight issues become visually distinct in one frame.

## What Changes

- Add a **dependency graph view** to the epic detail page, presented as a view toggle against the existing Linked Issues list (list ⇄ graph); the list view is preserved, not replaced.
- Render each linked issue as a node, colored by `status` (backlog / in_progress / done / cancelled), with a readiness marker distinguishing four states: 🟢 can start (`CanStart`), ⏳ waiting (`Blocker = WaitingFor(Issue)` showing the blocking `#N`), 🔄 in progress, ✓ done.
- Render directed edges from each prerequisite issue to the issue that depends on it (from `prerequisiteNumbers`), using `@xyflow/react` + `dagre` for automatic client-side layout.
- Make nodes clickable to navigate to the corresponding issue.
- Distinguish **external prerequisites** (prerequisite issues not belonging to this epic) — rendered as ghost/annotated nodes — so they are not misread as orphaned epic issues.
- Degrade gracefully: with 0–1 linked issues the graph is not rendered (list is shown); if a cycle is detected, the graph falls back to the list rather than rendering a broken layout.
- Extend the epic read model so the client can draw edges without per-issue fetches: `LinkedIssueDto` carries `prerequisiteNumbers` (and the readiness fields already consumed by `epic-board`).

## Capabilities

### New Capabilities

- `epic-dependency-graph`: DAG visualization on the epic detail page — view toggle against the Linked Issues list; nodes per linked issue colored by status with readiness markers (canStart / waiting-for #N / in-progress / done); directed prerequisite edges with client-side auto-layout; click-to-navigate; external-prerequisite distinction; graceful degradation for 0–1 issues and detected cycles.

### Modified Capabilities

- `epic-tracking`: Epic read-model contract — `LinkedIssueDto` (or an equivalent epic-scoped projection) exposes prerequisite edge data (`prerequisiteNumbers`, and the prerequisite summary needed to render external/ghost nodes) so the dependency graph can be computed client-side without N+1 issue fetches.

## Impact

- **Read model / API**: `EpicQuerier.GetLinkedIssuesAsync` and `LinkedIssueDto` gain `prerequisiteNumbers` (additive, backward-compatible field). Implementation decides between extending the DTO vs. a dedicated `/epics/{id}/dependency-graph` projection; small epic-scoped graphs favor extending the DTO.
- **Frontend**: New dependency-graph view on `EpicDetailPage` (view toggle with the existing Linked Issues list); new component(s) under `packages/web` wrapping `@xyflow/react` + `dagre`. `EpicListPage` and existing `epic-board` surfaces are untouched.
- **New dependencies**: `@xyflow/react` and `dagre` added to `packages/web` (frontend-only).
- **Consumed, not changed**: `issue-prerequisites` (edge source of truth), `issue-start-readiness` (`CanStart` / `Blocker`), `epic-board` (detail-page surfaces whose existing requirements still hold). These specs are not modified.
- **Constraints**: The graph is a read-only projection — no editing of dependencies from the graph. External (non-epic) prerequisites must be visually distinct. Acyclicity is assumed (domain guarantees via `issue-prerequisites`); cycles degrade to the list view.
- **Risk**: medium — introduces a new frontend rendering dependency and a read-model extension; epic-scoped node counts stay small so no server-side layout or large-graph optimization is in scope.
