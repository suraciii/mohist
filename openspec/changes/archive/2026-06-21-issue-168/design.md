## Context

Issue #168 fills the Dashboard `Pulse` zone slot that issue #163 left as an empty placeholder. The Dashboard (`packages/web/src/pages/dashboard/ui/DashboardPage.tsx`) currently maps over four zone ids and renders `DashboardZonePlaceholder` — a bare `<section data-testid="dashboard-zone-<id>" data-zone="<id>">` with no children — for each.

All data the Pulse view needs already exists:
- `useAgentActivity()` (`packages/web/src/entities/agent/api/queries.ts`) → `summary { active, waiting, completed, failed, slots }` + `sessions[]`. Polled every 5 s, cached by TanStack Query under `['agent-activity', params, projectId]`.
- `useActivityCards()` (`packages/web/src/widgets/coder-session/model/activity-cards.ts`) already projects that response into `activeCards`, `statusCounts`, and `slotUsage` via `sessionToCard`.
- `StatusBar` (`packages/web/src/shared/ui/StatusBar.tsx`) renders the `active/waiting/completed/failed` pills + `activeSlots/maxSlots` summary — used today by the Activity page.
- `ActiveSessionCard` (`packages/web/src/widgets/coder-session/ui/SessionCard.tsx`) is the full Activity-page card.
- `ContextHealthIndicator` + `classifyContextHealth` (`packages/web/src/widgets/session-health`) provide the shared `green|yellow|red` convention.

Constraint: pure read-only composite. No new query, no new endpoint, no domain write, no Activity-page replacement.

## Goals / Non-Goals

**Goals:**
- Render a Pipeline Pulse view into the existing `pulse` Dashboard slot.
- Show capacity (`active/max`) + `active/waiting/completed/failed` counts at the top.
- Show one compact card per active session with issue / stage / current task / task progress / token·cost / context-health color.
- Render an empty state when no active sessions exist.
- Reuse the Activity page's data path verbatim (same query, same projection).

**Non-Goals:**
- Replace or duplicate the Activity page.
- Render session transcript / replay (Session page owns that).
- Render historical capacity curves.
- Add new backend endpoints, queries, SSE events, or persistence.
- Fill the `attention`, `productivity`, or `digest` slots.

## Decisions

### D1. New `dashboard-pulse` widget, not a prop on `ActiveSessionCard`

Place a new widget at `packages/web/src/widgets/dashboard-pulse/` exporting `PulseZone`, with a dedicated `CompactSessionCard` component inside.

- **Rationale**: The spec requires a slimmed variant, not a full replication. `ActiveSessionCard` currently renders activity previews, an `ObservabilityBar` with model/tool/failure detail, and `ActiveSessionAnomalies` — none of which belong in a glanceable half-width zone. A dedicated compact component keeps the Pulse widget self-contained and avoids coupling two visual densities through a `compact` boolean that would slowly accrete conditional branches.
- **Alternative rejected**: Add a `compact` prop to `ActiveSessionCard`. Rejected because it complicates a fully tested component and mixes two presentation contracts in one file.
- **Reuse**: `CompactSessionCard` consumes the existing `SessionCard` type from `activity-cards.ts` and reuses `ContextHealthIndicator`, `formatCompact`, and `formatCost` helpers — no new data shape.

### D2. `CompactSessionCard` field selection

The compact card renders exactly the fields named in the spec; everything else is dropped:

| Field | In compact? | Source |
|---|---|---|
| Issue number | yes | `card.issueNumber` |
| Issue stage badge | yes | `card.issueStage` (shared `STAGE_COLORS` map) |
| Current task title | yes (fallback to `taskDescription`) | `card.title ?? card.taskDescription` |
| Task progress | yes, when present | `card.taskProgress` (compact `completed/total` + thin bar) |
| Token usage · cost | yes, single compact line | `card.totalTokens`, `card.costAmount/costCurrency` |
| Context-health color | yes | `ContextHealthIndicator` over `card.contextWindowUsed/Size/Percent` |
| Elapsed time, model, activity previews, tool/error counts, failure category, anomalies | **no** | dropped |

- **Rationale**: The Pulse zone is half the Dashboard width on `md+` viewports; clamping to the spec's named fields keeps each card to ~3 lines and lets 2–3 cards fit without scrolling.
- **Line clamping**: titles use the same `-webkit-line-clamp: 1` pattern as `ActiveSessionCard` so long task names cannot blow up the layout.

### D3. Dashboard zone mounting: `DashboardZone` wrapper replaces `DashboardZonePlaceholder`

Replace `DashboardZonePlaceholder` with a small `DashboardZone` presentational wrapper that renders the **same** slot container (`data-testid="dashboard-zone-<id>"`, `data-zone`, `aria-label`) and accepts optional `children`. `DashboardPage` decides per-zone what to render:

```tsx
function renderZoneContent(id: DashboardZoneId): React.ReactNode | null {
  switch (id) {
    case 'pulse': return <PulseZone />
    default: return null
  }
}
```

- Empty children → the slot renders with the placeholder appearance (dashed border, min-height), satisfying the MODIFIED `dashboard-shell` requirement that unimplemented slots stay empty.
- Non-empty children → the slot renders the capability's content with the same identity attributes, satisfying "Implemented zone slot renders its capability content".
- **Rationale**: One container component, one identity contract, one extension point (`renderZoneContent`) for future zones (Attention/Productivity/Digest).
- **Alternative rejected**: Keep `DashboardZonePlaceholder` and special-case `pulse` inline in `DashboardPage`. Rejected because it forks the container markup and hides the slot contract behind an inline conditional.

### D4. `PulseZone` layout: header + card list + empty state

```
PulseZone
├── PulseCapacityHeader  (active/max bar + 4 status pills, reuses StatusBar styling primitives)
├── if activeCards.empty:
│     EmptyState  ("No active sessions" muted affordance)
│   else:
│     list of <CompactSessionCard>  (cap at N=4; overflow → "View N more in Activity" link)
```

- **Capacity header**: render the same `active/max` and four counts the Activity page's `StatusBar` shows. Reuse the pill color tokens (`bg-blue-100`, `bg-amber-100`, `bg-green-100`, `bg-red-100`) so the two surfaces read identically.
- **Overflow cap**: when `activeCards.length > 4`, render the first 4 and a footer link `+N more` to the Activity page. Avoids an unbounded list inside a half-width zone.
- **Empty state**: rendered only in the card-list area; the capacity header still shows `0/max` per the spec scenario "Empty state does not suppress capacity header".
- **Alternative considered**: make the zone internally scrollable with `max-h` and render all cards. Rejected — a hidden scroll inside a dashboard tile is poor UX; capping + overflow link is clearer.

### D5. Data: call `useActivityCards()` directly, no new hook or query

`PulseZone` calls `useActivityCards()` (which wraps `useAgentActivity`). TanStack Query deduplicates by the shared `['agent-activity', params, projectId]` key, so when the Activity page and Dashboard are both mounted they share one cache entry and one network poll.

- **Rationale**: The spec requires "same source as Activity page, no separate query". `useActivityCards` is already that source, projected into the card shape both surfaces consume.
- **No `now` ticker**: `CompactSessionCard` does not render elapsed time, so the 1 s `setInterval` that `ActivityPage` uses is unnecessary. The 5 s query refetch + `animate-pulse` active dot provide liveness.

## Risks / Trade-offs

- **[Dashboard-shell tests will break]** The current `dashboard-shell` spec tests assert all four slots render as *empty* placeholders. After this change the `pulse` slot renders content. → Mitigation: those tests are updated in the same change to match the MODIFIED requirement ("unimplemented slots render empty; implemented slots render their capability content"). The dashboard-shell delta spec already encodes this new contract.
- **[Half-width overflow]** Compact cards in a `md:grid-cols-2` Dashboard could clip long titles or many cards. → Mitigation: single-line clamping on every text field, plus the N=4 cap + overflow link (D4).
- **[Visual divergence from Activity page]** Two card variants (`ActiveSessionCard` vs `CompactSessionCard`) could drift over time. → Mitigation: both consume the same `SessionCard` model type and the same `ContextHealthIndicator` / format helpers; only layout density differs. The spec explicitly scopes Pulse to a subset of fields, so divergence is intended.
- **[Stale data on first paint]** `useAgentActivity` is `enabled: !!projectId` and starts uncached on first Dashboard load. → Mitigation: acceptable; Pulse renders its empty/zero state until the first response arrives, same as the Activity page.
- **[Future zone coupling]** Adding the `renderZoneContent` switch in `DashboardPage` invites future zones to inline more cases. → Trade-off: acceptable for 4 total zones; if it grows, refactor to a registry map.

## Migration Plan

This is a pure frontend, read-only addition. No backend, schema, API, or persistence changes.

- **Deploy**: ship the new `dashboard-pulse` widget, the `DashboardZone` wrapper, and the `DashboardPage` wiring in one change. No feature flag is required given the low risk and read-only nature.
- **Test updates**: update `dashboard-shell` capability tests to reflect the MODIFIED requirement (pulse slot now has content; other three slots remain empty). Add `PulseZone` and `CompactSessionCard` unit tests covering: capacity header values, card field rendering, context-health color, empty state, overflow cap, and the shared-data-source contract (no separate query key).
- **Rollback**: revert the commit. Because no backend or domain state changed, rollback is clean — the `pulse` slot returns to being an empty placeholder.

## Open Questions

- **Overflow cap value**: is 4 the right max cards before the "View N more in Activity" link? Open to product feedback; trivially tunable via a constant.
- **Status pill styling**: should the Pulse capacity header reuse `StatusBar` directly (importing it) or just its color tokens? Leaning toward reusing tokens only, because `StatusBar` is a full top-bar layout (including `children` for runner badges) that does not fit a half-width zone. Confirm during build.
- **Link target for overflow**: should "+N more" link to `/activity` (project-scoped) or stay inside Dashboard? Leaning toward `/activity` since the Activity page owns the full list.
