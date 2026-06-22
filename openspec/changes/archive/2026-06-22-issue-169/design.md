## Context

Issue #169 (Epic #9) fills the Dashboard `productivity` zone slot — currently a dashed `DashboardZonePlaceholder` (`packages/web/src/pages/dashboard/ui/DashboardZonePlaceholder.tsx`) — with a real composition view. It is pure web-layer composition: three data sources are composed, no new server query is introduced.

Current state of the consumed contracts (verified in the worktree):

- **C — Issue completion (`issue-completion-metrics`, #165, done):**
  - Snapshot: `useCompletionSnapshot()` / `deriveCompletionSnapshot(issues)` at `packages/web/src/entities/issue/lib/completion-snapshot.ts:29` returns `{ completed, failed, new }` for a rolling 7-day window. Already wired; reused unchanged.
  - Trend endpoint: `GET /api/projects/{projectRef}/issues/metrics/completion?bucket=week` (`packages/server/src/Mohist.Server/Api/IssueRoutes.Metrics.cs:12`). Returns `{ bucket, window:{from,to}, buckets:[{boundary, completed, failed}, ...] }` — dense, 12 trailing ISO weeks, Mon-anchored UTC, deduped per (issue, type). **No web client function exists for it yet** — this issue adds the consumer, not a new endpoint.
- **D — Agent/Session usage (#166, marked done):** the per-session `AgentSessionUsage` shape exists (`packages/web/src/entities/coder-session/model/types.ts:5`: tokens / cost / context fields), but **no project-level usage aggregation endpoint or hook is present in the codebase**. This is the issue's documented low-risk degradation point ("若 C/D 未就绪，本 issue 可先接 Epic 进度+占位").
- **Epic progress (existing):** `useEpics()` (`packages/web/src/entities/epic/api/queries.ts:7`) returns `EpicWithProgress[]` with `progress.deliveredCount / totalIssueCount`. `EpicListPage.tsx:112-121` already renders this ratio as a width-% bar — a directly reusable pattern.
- **Slot host:** `DashboardPage.tsx:10-67` maps four zones through `DashboardZonePlaceholder`; the existing test (`DashboardPage.test.tsx:79-85`) asserts `dashboard-zone-productivity` with `data-zone="productivity"`.

Stakeholders: any returning user (satisfaction signal); no other surface depends on this zone.

## Goals / Non-Goals

**Goals:**
- Replace the empty `productivity` slot with a composed view: weekly completion snapshot (C), ≥2 in-progress Epic progress bars (`useEpics`), completion trend (C endpoint, by-week), and a collapsed "investment" panel (D).
- Preserve the dashboard-shell slot identity (stable `data-testid` / `data-zone`) so the host contract and existing tests stay meaningful.
- Render every section with its own empty state; never render a blank/broken panel.
- Add zero server routes, zero persistence fields, zero npm dependencies.

**Non-Goals** (per issue):
- No new backend endpoint or domain query.
- No streak / badges / ranking / cross-project aggregation.
- No configurable time range or bucket size (use C's fixed `week` bucketing).
- No swap of the snapshot from client derivation to the endpoint (that is #165's future reservation; out of scope here).

## Decisions

### D1. File layout — new `productivity` folder under the dashboard page
Place the zone at `packages/web/src/pages/dashboard/productivity/` with small single-purpose sub-components (snapshot row, epic-progress list, trend chart, investment panel) plus an index `ProductivityZone.tsx`. Mirrors the existing `pages/<feature>/ui/` convention while keeping the productivity composition self-contained. The other three zones keep using `DashboardZonePlaceholder`.

*Alternative considered:* a single flat `ProductivityZone.tsx` file — rejected because the four sections each have their own empty-state and data-hook concerns and would compose poorly in one file.

### D2. Slot contract preserved; only inner content swaps
`DashboardPage.tsx` keeps rendering a wrapper carrying `data-testid="dashboard-zone-productivity"` and `data-zone="productivity"` for the productivity slot, but the wrapper now mounts `<ProductivityZone/>` instead of `DashboardZonePlaceholder`. The other three zones are unchanged. This keeps the dashboard-shell slot identity stable, satisfies the MODIFIED `dashboard-shell` scenario, and means the existing test's element lookup still works — it just gains new inner-content assertions.

*Alternative considered:* introduce a new testid and delete the placeholder — rejected because it needlessly breaks the slot-identity contract that downstream zones also rely on.

### D3. Snapshot reuses `useCompletionSnapshot()` unchanged
The weekly `completed/failed/new` row reads `useCompletionSnapshot()` directly. Per #165's reservation contract, the client-derivation approximation (`updatedAt` as completion time) is an accepted v1 limitation for the snapshot; swapping to the endpoint is a future concern, explicitly excluded by the Non-Goals.

### D4. Trend adds a *consumer* of C's existing endpoint (not a new query)
Add `fetchCompletionTrend(projectId)` + `useCompletionTrend()` under `packages/web/src/entities/issue/api/` (next to `client.ts`). It calls the already-shipped `GET .../issues/metrics/completion?bucket=week`, hard-codes `bucket=week`, TanStack Query key `['issues','metrics','completion','week', projectId]`, modest `staleTime` (e.g. 60s) since the dashboard re-lands often. This is a new web *client* of an existing route — explicitly permitted by the "no new domain query" Non-Goal, which scopes to server-side queries/endpoints.

*Alternative considered:* compute the trend client-side from the loaded issue list — rejected: the issue list has no reliable historical completion timestamps (`updatedAt` misattributes), and #165 shipped the endpoint precisely to fix that; recomputing client-side would duplicate wrong semantics.

### D5. Trend rendering — inline SVG sparkline, no chart library
Render a small inline `<svg>` with a `<polyline>` (completed counts over the 12 weekly buckets), zero-axis baseline, and minimal hover/title text per point. No axes chrome beyond what the satisfaction signal needs ("is the curve going up"). No new dependency.

*Alternatives considered:* (a) recharts/visx — rejected (Non-Goal: no chart lib); (b) weekly bars reusing the Epic progress bar style — rejected (the issue's user voice explicitly asks for a "曲线"/curve, and a line reads as trend where bars read as discrete totals).

### D6. Epic progress reuses the existing bar pattern; "in-progress" = `EpicStatus.Active`
Filter `useEpics()` to `status === EpicStatus.Active`, sort by priority then `deliveredCount/totalIssueCount`, render up to a small cap (start with 3) using the exact width-% bar pattern from `EpicListPage.tsx:112-121`. Guard `totalIssueCount === 0` → 0% fill (no divide-by-zero). Render the Epic empty state when fewer than 2 active Epics exist (AC #2).

*Open:* the cap ("≥2") is a soft floor, not a ceiling — see Open Questions.

### D7. Investment (D) ships structural, degrades to empty state
Build the investment panel as a collapse-by-default section with a dedicated caliber-annotation slot. Because D's aggregated usage surface is absent from the codebase (verified: no endpoint, no hook), the panel renders an explicit "data unavailable" empty state at launch. The collapse/expand behavior and caliber-annotation layout are still exercised by tests, so the spec is satisfied structurally and the panel is wire-ready: when D's hook lands, only the body swaps from empty-state to figures.

*Alternatives considered:* (a) block the issue on D — rejected (issue explicitly permits degradation, and the other three sections deliver the core satisfaction signal); (b) compute a client-side usage rollup from per-session `AgentSessionUsage` — rejected (requires loading all project sessions = a new query path, violating the Non-Goal).

### D8. Per-section empty states; zone never blank
Each sub-component owns its own empty state (no issues / no active Epics / no trend buckets / usage unavailable). The zone composes them vertically; if all four are empty it still renders four labeled empty-state blocks rather than disappearing.

## Risks / Trade-offs

- **[D's aggregated usage surface is absent in the codebase]** → Investment panel degrades to an explicitly-labeled empty state; collapse/expand + caliber-annotation are still tested; panel is wire-ready for D. No blocker for the core satisfaction signal (snapshot + Epic bars + trend).
- **[Existing test asserts the productivity placeholder]** → Keep the slot wrapper's `data-testid`/`data-zone` and update `DashboardPage.test.tsx` to assert the productivity slot now contains the zone content while the other three slots remain empty placeholders. Also add focused `ProductivityZone.test.tsx` coverage (snapshot counts, ≥2 bars + <2 empty state, trend presence, investment default-collapsed + caliber annotation on expand, all-data-empty).
- **[Client snapshot uses `updatedAt`; endpoint uses precise completion time]** → Two different completion-time semantics coexist on the page (snapshot = approximate, trend = precise). Accepted: it matches #165's documented reservation contract and the Non-Goal that defers the snapshot swap. The caliber annotation principle (D7) is the template for disclosing such baselines if the snapshot ever needs one.
- **[Fixed 12-week trend window]** → Matches the Non-Goal (no configurable range); if users later want a range, C grows it, not this zone.
- **[Inline SVG trend is visually minimal]** → Trade-off accepted against pulling in a charting dependency; the sparkline is sufficient for the "is productivity trending up" signal.

## Migration Plan

- **Deploy:** web-only change; no server deploy, no migration, no feature flag (local-first app). Ship in a single PR.
- **Sequence:** (1) add `useCompletionTrend` client+hook; (2) build the four sub-components + `ProductivityZone`; (3) swap the productivity slot in `DashboardPage.tsx`; (4) update `DashboardPage.test.tsx` and add `ProductivityZone.test.tsx`; (5) `npm run typecheck -w packages/web` + `npm run test:run -w packages/web`.
- **Rollback:** revert the PR — the slot returns to `DashboardZonePlaceholder`; C/D/`useEpics` are untouched. No data or persistence impact.

## Open Questions

1. **D's actual surface:** is the Agent/Session usage aggregation genuinely absent, or did #166 land under a name not found in the worktree? If a hook exists, wire D7's body to it directly; otherwise keep the empty state and file a follow-up.
2. **Snapshot vs endpoint semantics:** keep `useCompletionSnapshot` (client derivation, `updatedAt`) for the snapshot, or align it with the endpoint's precise completion time now? Default: keep the client derivation (Non-Goal defers the swap).
3. **"In-progress" Epic definition:** `EpicStatus.Active` only, or also include recently-`Done`/not-`Closed`? Default: `Active` only.
4. **Epic bar ceiling:** AC sets a floor of ≥2 but no cap. When many Epics are active, show top 3 by priority with a "+N more" affordance, or show all? Default: cap at 3 with "+N more".
5. **Trend metric:** plot `completed` only, or overlay `failed` as a secondary color? Default: `completed` only for v1 (matches the satisfaction-curve user voice); `failed` stays in the snapshot number.
