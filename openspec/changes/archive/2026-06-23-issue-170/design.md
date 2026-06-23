## Context

Issue #170 fills the Dashboard `Digest` zone slot reserved by the prerequisite `dashboard-shell` capability (#163). The slot currently renders an empty `DashboardZonePlaceholder`; this change mounts real zone content — a read-only, at-a-glance "what happened recently" summary of completed / failed / archived issues.

Current state:
- `packages/web/src/pages/dashboard/ui/DashboardPage.tsx` maps over four zone ids (`attention`, `pulse`, `productivity`, `digest`) and renders `DashboardZonePlaceholder` for each. The Digest slot is `data-testid="dashboard-zone-digest"`.
- Data sources already exist and are read-only:
  - `useIssues({ projectId })` — active issues (includes `done` / `cancelled`).
  - `useArchivedIssues({ projectId })` — filters `archivedAt != null`.
  - `events-hub.ts` — SignalR stream feeding the Activity page (only relevant if the optional activity summary is included).
- The project's failure taxonomy is already established by `entities/issue/lib/completion-snapshot.ts`: **completed = `status === 'done'`**, **failed = `status === 'cancelled'`**. The Digest must reuse this definition, not invent a new one.
- Shared affordances exist: `useProjectPath()` for issue-detail links, `formatTimeAgo(date: Date)` in `shared/lib/format-time.ts` for relative timestamps.

Constraints:
- **No new backend endpoints** (acceptance criterion #4). Pure frontend composition.
- **No configurable N** (non-goal). Top-N is a fixed constant.
- Must not replace the Activity page or add filtering (non-goals).
- Stakeholders: Epic #9 (Dashboard — default home). Sibling zones Attention/Pulse/Productivity are separate issues; this change must not block them.

## Goals / Non-Goals

**Goals:**
- Mount real content into the Dashboard `digest` zone slot, replacing its placeholder.
- Show top-N recently completed / failed / archived issues, each jumpable to issue detail, with a relative timestamp.
- Render a distinct empty state when there is no recent history, and a loading state while queries resolve.
- Reuse the existing failure taxonomy (`completion-snapshot`) so "completed"/"failed" mean the same thing everywhere.
- Keep DashboardPage thin and the zone contract intact for the three remaining zones.

**Non-Goals:**
- Activity event summary is **deferred** (see Decisions); not required by AC #1.
- No time-window filtering, no configurable N, no event filtering/search.
- No backend changes, no new queries, no mutation of domain state.
- No reuse of the `RecentCard` *component* (its `SessionCardType` props don't match `Issue`); only its *patterns* are referenced.

## Decisions

### D1. Placement: a `widgets/dashboard-digest` widget + pure derivation in `entities/issue/lib`

The zone content is a composition over entity data — the same role `widgets/coder-session` plays for the Activity page. Place the view in `widgets/dashboard-digest/ui/`. Place the categorize/sort/slice logic as a pure function `deriveRecentDigest(issues, archivedIssues, { topN })` plus a `useRecentDigest()` hook in `entities/issue/lib/recent-digest.ts`, mirroring the established `deriveCompletionSnapshot` / `useCompletionSnapshot` precedent. This keeps derivation unit-testable independent of React.

- **Alternatives considered:**
  - Co-locate the view inside `pages/dashboard/ui/`. Rejected — couples zone content to the page and buries entity-derived logic in a page folder; the dashboard-shell contract treats slots as independent mount points.
  - Compute inside the component. Rejected — loses the pure-function testability that `completion-snapshot` already establishes for this exact kind of derivation.

### D2. Mount into the slot via conditional render in DashboardPage (not a registry)

In `DashboardPage`, render the `dashboard-digest` widget when `zone.id === 'digest'`, otherwise keep `DashboardZonePlaceholder`. The `data-testid="dashboard-zone-digest"` and `data-zone="digest"` contract are preserved on the wrapper so existing tests and the dashboard-shell identity scenario still hold.

- **Alternatives considered:**
  - A zone-id → component registry (`{ digest: DashboardDigestWidget }`) so the 2nd/3rd/4th zones slot in without re-touching DashboardPage. Viable and barely more code, but YAGNI with only one filled zone today. Revisit when the second zone lands — the conditional naturally becomes a registry lookup.

### D3. Categorization reuses the existing taxonomy; ordering by recency, no time window

- completed: `status === 'done'`, ordered by `updatedAt` desc.
- failed: `status === 'cancelled'`, ordered by `updatedAt` desc. (Matches `completion-snapshot` exactly.)
- archived: `archivedAt != null`, ordered by `archivedAt` desc.
- Each sliced to top-N.

No 7-day window is applied. `completion-snapshot` uses a window because it *counts* events in a period; the Digest *ranks by recency* and takes a fixed N, which already bounds the result. A hard window would risk rendering an empty digest for a dormant project even when older-but-relevant history exists. The relative timestamp makes staleness visible to the user.

- **Alternatives considered:**
  - Reuse the 7-day window. Rejected (above).
  - Treat `health === 'interrupted'` as failed too. Deferred — would diverge from the established `completion-snapshot` failure definition; align now, broaden later if users report missed failures.

### D4. Fixed `DIGEST_TOP_N = 5` per category

Exported constant from the widget (or lib). Not user-configurable (non-goal). 5 rows per category fits a half-width dashboard tile without overflow; three categories × 5 = at most 15 compact rows.

### D5. Activity event summary is deferred

AC #3 explicitly gates the activity summary on "若纳入" (if included); AC #1 requires only the three issue categories. Deferring keeps scope tight, avoids coupling the Digest to the session-centric `useActivityCards` shape, and lets the activity summary's exact presentation be validated separately. When added, it will reuse `useActivityCards()` (same events-hub source as Activity page) sliced to top-N — satisfying AC #3 by construction. The spec already encodes this as optional.

- **Alternatives considered:**
  - Include now via `useActivityCards().recentCards` top-N. Cheap and same-source, but pulls session-card semantics into the dashboard and expands test surface for an optional feature. Rejected for this issue.

### D6. Build a slim `DigestRow`, do not reuse `RecentCard`

`RecentCard` expects a `SessionCardType` (sessionId, issueStage, completedAt, etc.) that does not match the `Issue` shape. The issue body references reusing RecentCard's *patterns* (number + title + relative time + jump link), not the component. A purpose-built `DigestRow` keyed on `Issue` is clearer and avoids an adapter. It will reuse the same affordances `RecentCard` uses: `useProjectPath()` → `toProjectPath('/issues/<number>')` and `formatTimeAgo(new Date(iso))`.

### D7. Loading vs empty state

`useIssues`/`useArchivedIssues` expose `isLoading`. While either is loading → render a lightweight skeleton/spinner inside the zone. Once both resolve and all three categories are empty → render an empty-state message (e.g. "No recent activity"). This directly satisfies the spec's "empty state is distinct from loading" scenario.

## Risks / Trade-offs

- [Client-side sort over all project issues could grow with large projects] -> Mitigation: Mohist is local-first / single-developer; issue volumes are modest. top-N via slice after sort is O(n log n) on an already-in-memory list. AC #4 forbids new endpoints, so backend pagination is explicitly out. Acceptable trade-off, documented.
- [No time window means a dormant project shows stale rows as "recent"] -> Mitigation: relative timestamps surface staleness transparently; users can drill into Activity for the full stream. This is a deliberate trade-off (D3), not a bug.
- [`status === 'cancelled'` may miss in-flight failures that weren't cancelled] -> Mitigation: align with the existing `completion-snapshot` taxonomy for consistency; broaden the predicate later if feedback indicates gaps.
- [Conditional render in DashboardPage must not break the dashboard-shell slot-identity contract] -> Mitigation: preserve `data-testid="dashboard-zone-digest"` and `data-zone="digest"` on the rendered wrapper; existing DashboardPage tests assert on these and will be updated to assert content vs placeholder per zone.

## Migration Plan

This is a pure frontend change. No backend, database, config, or data migration.

- **Deploy:** merge the change; the Dashboard route begins rendering Digest content. No feature flag (low risk, personal-local product).
- **Verify post-deploy:** open Dashboard for a project with mixed recent history → three categories render and rows jump to issue detail; open for a fresh/empty project → empty state; throttle network → loading state.
- **Rollback:** revert the commit. DashboardPage returns to rendering the placeholder for `digest`. No data side-effects; `dashboard-shell` behavior is unchanged.

## Open Questions

- **Top-N value:** proposed `5`. Confirm during design review; trivially tunable via the constant.
- **Failure predicate:** should `failed` eventually include `health === 'interrupted'` in addition to `status === 'cancelled'`? Deferred pending user feedback.
- **Activity summary timing:** when to schedule the deferred activity top-N (D5), and whether it reuses `useActivityCards` directly or gets a thin digest-specific selector.
