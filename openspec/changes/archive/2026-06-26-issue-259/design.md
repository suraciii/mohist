## Context

The dashboard's `Pulse` zone is an empty placeholder today: `DashboardPage.tsx:75-83` maps over `DASHBOARD_ZONES` and mounts `<DashboardDigestWidget />` only for the `digest` branch, leaving `pulse` (and `productivity`) as bare `<DashboardZone />` with no children. The `PulseZone` widget (`packages/web/src/widgets/dashboard-pulse/`) is already built and fully tested — it renders runner slot usage, four status pills, real-time `CompactSessionCard`s for active in-flight sessions (capped at 4 with an Activity overflow link), and a "No active sessions" empty state. It is **never imported** outside its own widget folder and tests.

The first-screen information architecture was settled by #258 (merged): headline → full-width `AttentionHero` → three equal-weight zone slots (`Pulse`, `Productivity`, `Digest`) beneath. The `pulse` slot is the designated mount point.

Data is already available read-only: `PulseZone()` takes no props and derives everything from `useActivityCards()` (`widgets/coder-session/model/activity-cards.ts:113`), which memoizes over `useAgentActivity()` (`entities/agent/api/queries.ts:25`) — project-scoped, `refetchInterval: 5000`. No backend, API, or domain-state changes are involved.

Constraints:
- The widget's internal behavior is fixed by its spec; this change only mounts it.
- The dashboard page test (`DashboardPage.test.tsx:126-155`) currently asserts `pulse` is an empty placeholder (`childElementCount === 0`, `border-dashed`); those assertions become false once mounted.

## Goals / Non-Goals

**Goals:**
- Mount `<PulseZone />` into the `pulse` zone slot, replacing the empty placeholder, mirroring how `digest` mounts `DashboardDigestWidget`.
- Update the dashboard page test to assert Pulse is mounted (not a placeholder) and that the widget lives inside the correct slot without leaking into siblings.
- Keep the existing `PulseZone` / `CompactSessionCard` widget tests passing unchanged.

**Non-Goals:**
- No changes to `PulseZone` or `CompactSessionCard` behavior, props, or internal logic.
- No backend / API / domain-state changes; no new endpoint; no write operations.
- No ETA prediction or activity ticker (explicitly excluded by spec as false-value signals).
- No work on the `Productivity` slot (still an empty placeholder pending its downstream issue).

## Decisions

### D1. Mount via the existing `DashboardZone` children branch

In `DashboardPage.tsx`, extend the `.map` branch selector from `zone.id === 'digest'` to a small selector that mounts `digest` → `DashboardDigestWidget` and `pulse` → `PulseZone`, leaving `productivity` as the bare `<DashboardZone />`. Import `PulseZone` from `../../../widgets/dashboard-pulse` (mirrors the `dashboard-digest` import path, consistent with the `widgets/` placement convention).

The zone wrapper itself is unchanged: `DashboardZone` already renders the same `<section data-testid="dashboard-zone-{id}">` whether or not children are passed — mounting only adds children inside it. `PulseZone` renders its own root `data-testid="pulse-zone"`.

**Alternatives considered:**
- *Render `<PulseZone />` outside `DashboardZone`* — rejected: breaks the uniform zone-slot contract and the test conventions that assert widgets live inside `dashboard-zone-{id}`.
- *A generic zone→widget registry map* — rejected as over-engineering for three slots with two mounted; the explicit branch selector mirrors the existing `digest` pattern and stays readable.

### D2. Dashboard test mocks the activity source, not the widget

`PulseZone` calls `useActivityCards()` → `useAgentActivity()`, which the dashboard test does not currently mock. To keep the dashboard test deterministic and independent of the widget's own coverage, mock at the data-hook boundary: `vi.mock('@/widgets/coder-session/model/activity-cards', ...)` returning a `useActivityCards` stub that yields an empty-card view-model (`activeCards: []`, zeroed `statusCounts`, `{active:0,max:0}` `slotUsage`). This renders `PulseZone` into its **empty state** (`pulse-empty-state`) without needing session fixtures — the dashboard test only needs to prove *mounting*, not card rendering (already covered by `PulseZone.test.tsx`).

**Alternatives considered:**
- *Mock `useAgentActivity` at `entities/agent/api/queries`* — works but couples the dashboard test to the lower-level query shape; mocking the derived `useActivityCards` view-model is simpler and more stable.
- *Mock the `dashboard-pulse` module entirely (`vi.mock('.../dashboard-pulse')`)* — rejected: it would not prove the real widget is mounted in the right slot, only that *something* is. The spec requires the `dashboard-pulse` zone content to be mounted.
- *Provide full session fixtures so cards render* — unnecessary; card rendering is the widget's contract, verified in its own test file.

### D3. Test assertions updated to "mounted, in-slot, non-leaking"

Update the test at `DashboardPage.test.tsx:126-155` so the `pulse` slot is asserted like `digest`:
- `pulse.childElementCount > 0` (it is no longer an empty placeholder). Note: `DashboardZone` renders `border-dashed` **unconditionally** whether or not children are passed (see `DashboardZone.tsx`), so the mounted `pulse` and `digest` zones are both `border-dashed`; `border-dashed` is therefore not a distinguishing assertion. The existing `digest` test mirrors this — it asserts containment only and never checks `border-dashed`.
- `pulse` contains `[data-testid="pulse-zone"]` (or `pulse-empty-state` given D2's empty fixture).
- `pulse` does **not** contain the digest widget's testid, and `digest`/`productivity` do not contain `pulse-zone` (no leakage, matching the existing negative-assertion convention).

`productivity` remains asserted as the sole empty placeholder — the only zone with `childElementCount === 0`.

## Risks / Trade-offs

- **[Risk] Dashboard test now depends on a data hook mock** → Mitigation: mock at the `useActivityCards` boundary with a minimal empty view-model; the stub is tiny and stable. The dashboard test does not assert on activity data, only on zone mounting.
- **[Risk] Live 5s refetch could cause dashboard test flakiness** → Mitigation: mocking `useActivityCards` removes the underlying `useQuery`/`refetchInterval` from the test entirely; no timers are involved.
- **[Trade-off] Mocking the data hook vs. the widget module** → chose the data hook (D2) so the test still exercises the real `PulseZone` mount point; slightly more setup than a full module mock, but worth the fidelity.

## Migration Plan

1. **Single PR, frontend-only:** add the `PulseZone` import and branch in `DashboardPage.tsx`; update `DashboardPage.test.tsx` (add the `useActivityCards` mock, flip the pulse assertions).
2. **Verify:** run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; confirm `PulseZone.test.tsx` / `CompactSessionCard.test.tsx` pass unchanged alongside the updated dashboard test.
3. **Rollback:** revert the single PR — the widget and its tests are untouched, so removal cleanly restores the empty placeholder with no schema or backend consequences.

## Open Questions

None — the mount point, widget contract, data source, and test conventions are all established by #258 and the existing widget. The only implementation-level choice (mock boundary) is resolved in D2.
