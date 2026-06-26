## Context

The dashboard (`packages/web/src/pages/dashboard/ui/DashboardPage.tsx:57`) currently renders a single `grid-cols-1 md:grid-cols-2` container (`dashboard-zones`) wrapping four equal-weight `DashboardZone` slots — `attention`, `pulse`, `productivity`, `digest`. Of these, only `digest` mounts real content (`DashboardDigestWidget`); the other three render as empty dashed-border placeholders (`DashboardPage.tsx:67`). The user's most important question — "what needs me right now?" — is buried as an equal-weight peer, and the `AttentionHero` widget already built under `packages/web/src/widgets/attention-hero/` is **not wired into the page at all**.

Current state facts relevant to this design:
- `AttentionHero` (`widgets/attention-hero/ui/AttentionHero.tsx`) is feature-complete: it renders its **own** `<section data-testid="dashboard-zone-attention" data-zone="attention">` shell, derives items via the shared pure function `deriveAttentionItems` (`entities/issue/model/attention.ts:21`), already shows a one-line `detail` per item (issue `title`, or `blockedReason` fallback for blocked), already offers inline Approve/Resume + jump links, and already handles loading / all-clear / runner-down states. It is covered by a 670-line test suite.
- Data sources are read-only and pre-existing: `useIssues` (`entities/issue/api/queries.ts:26`, disabled unless `projectId` set) and `useAgentStatus` (`entities/agent/api/queries.ts:6`, polls every 5s, exposes optional `runnerAvailable`).
- `Issue` has `status`, `health`, `approvalState?.status`, `workflowStage`, `updatedAt`, `title`, `blockedReason` — everything the headline and Hero need.
- No factory-status widget exists; no backend endpoint is needed for this change (the cost rollup endpoint is downstream issue #262).
- Styling is Tailwind v4 + shadcn tokens; tests are Vitest + @testing-library/react + jsdom with a hoisted-`vi.mock` pattern.

Constraints: no new backend API, no new domain write state beyond the existing approve/resume actions, visual style stays consistent with existing widgets.

## Goals / Non-Goals

**Goals:**
- Replace the equal-weight 2×2 zone layout with a vertical first-screen composition: full-width **factory status headline** → full-width **Attention Hero** → remaining equal-weight zones (`Pulse`, `Productivity`, `Digest`) beneath.
- Wire the existing, already-tested `AttentionHero` into the dashboard as the dominant full-width Hero.
- Add a read-only factory-status headline surfacing runner-online, in-flight count, awaiting-approval count, today-shipped count, plus a reserved (initially empty) today-cost slot.
- Keep zone slot identities stable (`attention`, `pulse`, `productivity`, `digest`) so downstream zone issues (#259/#260) can target them.
- Isolate "today" derivation so the future `completedAt` migration is a single-source change.

**Non-Goals:**
- No Pulse / Productivity / Digest content (separate issues).
- No cost rollup endpoint — only the reserved empty slot.
- No new attention rules, no aging, no batch approve, no failure clustering.
- No changes to the issue approve/resume API or domain state.
- No mobile-specific redesign beyond the existing responsive grid behavior.

## Decisions

### Decision 1: Vertical stack composition, not a single spanning grid

DashboardPage renders three vertically-stacked containers instead of one grid:
1. `dashboard-headline` — full-width `FactoryStatusHeadline`.
2. `dashboard-hero` — full-width `AttentionHero` (mounted directly, see Decision 2).
3. `dashboard-zones` — the existing 2-col grid, but now holding only the three remaining zones (`pulse`, `productivity`, `digest`), with `digest` mounting `DashboardDigestWidget`.

**Rationale:** Full-width elements and equal-weight peers have different semantics and ordering constraints; putting them in the same grid via `col-span-2` couples ordering to grid flow and makes "headline above hero above zones" harder to assert. Distinct containers give stable, independently-addressable mount points and straightforward DOM-order tests.

**Alternative considered:** Keep one grid and give headline/hero `md:col-span-2`. Rejected — it entangles full-width and half-width items in the same flow (e.g. a 1-cell zone could slot beside a full-width item on some breakpoints), and obscures the "headline is topmost" invariant. Also considered: a generic `<DashboardZone full>` wrapper variant; rejected as unnecessary indirection when the Hero and headline are single-purpose components.

### Decision 2: Mount AttentionHero directly, not inside DashboardZone

`AttentionHero` already renders its own `<section data-testid="dashboard-zone-attention" data-zone="attention">` shell with purpose-built emerald/amber/red styling (distinct from the dashed placeholder shell). Mounting it **inside** a `DashboardZone` would produce nested duplicate `data-testid="dashboard-zone-attention"` elements and bury its styling inside the dashed border. So the page mounts `<AttentionHero />` directly in the `dashboard-hero` slot.

**Rationale:** Preserves the existing, tested shell contract and the Hero's distinct visual identity with zero changes to the 670-line `AttentionHero.test.tsx`.

**Alternative considered:** Refactor `AttentionHero` to drop its own shell and be composed as `<DashboardZone><AttentionHero/></DashboardZone>`. Rejected — larger blast radius (rewrites the widget + its full test suite) for no functional gain, and the dashed placeholder styling is wrong for a dominant Hero.

### Decision 3: FactoryStatusHeadline is a new widget mirroring the attention-hero pattern

Create `packages/web/src/widgets/factory-status/` with `FactoryStatusHeadline.tsx` + barrel export, following the same structure as `attention-hero/` (props accept `issues?`/`agentStatus?` for DI, falling back to `useIssues`/`useAgentStatus`; read-only).

**Rationale:** Keeps `DashboardPage` as a thin composition shell (consistent with how `digest` already works) and makes the headline independently unit-testable. The proposal explicitly calls for "New factory-status widget under `packages/web/src/widgets/`."

**Alternative considered:** Inline the headline JSX in `DashboardPage`. Rejected — bloats the composition shell and couples derivation logic to the page, hurting testability.

### Decision 4: Field derivation lives in a pure function

Add `deriveFactoryStatus(issues, agentStatus): FactoryStatusFields` in the widget's model layer (mirroring `deriveAttentionItems`). `FactoryStatusHeadline` calls it via `useMemo`. The function returns `{ runnerAvailable: boolean; inFlight: number; awaitingApproval: number; shippedToday: number; todayCost: undefined }`.

- `inFlight` = count where `status === 'in_progress' && health !== 'done' && health !== 'cancelled'`.
- `awaitingApproval` = count where `approvalState?.status === 'awaiting'`.
- `shippedToday` = count where `status === 'done' && isTodayLocal(updatedAt)` (see Decision 6).
- `runnerAvailable` = `agentStatus?.runnerAvailable === true`.
- `todayCost` = always `undefined` this change (reserved).

**Rationale:** A pure function is unit-testable without rendering and keeps the component thin — exactly the pattern `entities/issue/model/attention.ts` established.

**Alternative considered:** Compute inline with `useMemo` in the component. Rejected — mixes derivation with rendering and forces DOM-based tests for pure logic.

### Decision 5: Today-cost slot renders an explicit placeholder, never zero

The headline reserves a today-cost field position. While `todayCost === undefined`, it renders a distinct placeholder (e.g. an em-dash `—` with `data-testid="factory-cost-reserved"`), never the numeric `0`, so an empty slot is visibly distinct from a computed zero-cost value. The field is driven by a single optional `todayCost` value on the derived status object, so #262 only needs to populate it.

**Rationale:** Spec requires the empty slot be distinguishable from zero and be connectable without restructuring layout.

**Alternative considered:** Conditionally render the field only when #262 lands. Rejected — spec requires the slot to ship visible now.

### Decision 6: "Today" is local-calendar-day on `updatedAt`, isolated in a helper

`isTodayLocal(iso)` compares the `updatedAt` ISO string against the user's local calendar day. The helper is the single seam for the future `completedAt` migration: swapping the source field inside the derivation function changes the data source without altering the user-visible "today shipped" contract.

**Rationale:** No backend endpoint exists; derivation must be client-side. Isolating it bounds the `completedAt` migration to one function.

**Alternative considered:** UTC day. Rejected — a personal "control room" reads in local time; UTC would show yesterday's work as today (or vice versa) near midnight.

## Risks / Trade-offs

- **[Duplicate `dashboard-zone-attention` testid]** if a future change wraps `AttentionHero` in a `DashboardZone`. → Mitigation: mount directly per Decision 2; add a regression assertion in `DashboardPage.test.tsx` that exactly one `dashboard-zone-attention` element exists and it is the Hero (not a dashed placeholder).
- **[Breaking the existing "keeps attention… as empty placeholders" test]** (`DashboardPage.test.tsx:126`) asserts attention is `border-dashed` with 0 children. → Mitigation: update that test — attention is now the mounted Hero (non-dashed, with content); keep asserting `pulse`/`productivity` remain empty placeholders.
- **[`updatedAt` is an approximation of completion time]** an issue marked `done` yesterday but touched today counts as shipped-today. → Mitigation: documented interim behavior; spec explicitly accepts `updatedAt` until `completedAt` lands (#262-adjacent), and the derivation is isolated (Decision 6) so the fix is one line.
- **[Timezone drift for "today"]** users near midnight or in non-local TZs may see off-by-one counts. → Mitigation: local-day is the right default for a personal control room; acceptable trade-off, revisit if reported.
- **[Empty `todayCost` could read as "broken"]** rather than "reserved". → Mitigation: explicit placeholder styling + `data-testid` distinct from a populated field, per Decision 5.
- **[`agentStatus.runnerAvailable` is optional]** so `undefined` must not read as "down" in either surface. → Mitigation: headline uses `=== true` for up and treats `undefined`/`false` as "unavailable-ish" with a neutral indicator; Hero already uses `=== false` for runner-down (unchanged).

## Migration Plan

This is a pure frontend change with **no backend, API, schema, or data migration**.

**Deploy:**
1. Add `factory-status` widget + `deriveFactoryStatus` + `isTodayLocal` helper with unit tests.
2. Restructure `DashboardPage.tsx` to the vertical stack (Decision 1) and mount `AttentionHero` (Decision 2).
3. Update `DashboardPage.test.tsx`: attention is now the Hero (not a placeholder); keep pulse/productivity placeholder assertions; add headline-rendering + slot-order assertions.
4. Run `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; all green before merge.

**Rollback:** Revert the single PR; the page returns to the 2×2 placeholder grid. No data effects, no coordination needed.

**Forward wiring (#262):** When the cost rollup endpoint lands, populate the headline's `todayCost` field from a new read-only query inside `FactoryStatusHeadline`; no layout or contract change required.

## Open Questions

- **`runnerAvailable === undefined` headline semantics:** Should an unresolved/unknown runner state display distinctly from "known down"? Current leaning: treat `true` as up, everything else as neutral-unavailable (distinct from the Hero's runner-down alert, which fires only on strict `=== false`). Confirm during implementation.
- **Headline visual density:** Five fields (four live + one reserved) across full width — confirm whether they render as an inline stat row or a compact card. Defer to implementation against existing widget styling; not a contract decision.
