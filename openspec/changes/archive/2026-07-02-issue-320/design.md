## Context

The Dashboard 首页 (`packages/web/src/pages/dashboard/ui/DashboardPage.tsx`) is meant to answer "is the system healthy, and is there anything that needs me?" at a glance. Over time it has accumulated four pieces of low-value / duplicated UI that force the user to scroll and self-filter:

1. A stale "Productivity preview will appear here once it ships." placeholder block inside the Attention Hero's All-clear state — transitional copy left over from when Productivity was not yet shipped. Productivity now renders as its own zone directly beneath the Hero (`ProductivityZone.tsx`), making the placeholder self-contradicting.
2. An "Ask Agent" button in the dashboard hero whose semantics are wrong for the dashboard (attention is for human approval/resume, not agent dispatch) and whose entry point is already covered by the header's New Issue control and the Pulse session cards.
3. Four lifecycle status pills (`Active`/`Waiting`/`Completed`/`Failed`) in the Pulse zone that duplicate the Factory status headline (`In flight` / `Awaiting approval`) and the Digest zone's recent completion list.
4. A `SnapshotRow` ("This week — Completed/Failed/New") in the Productivity zone that overlaps the Digest zone's recent completed/failed list (which carries issue titles + links at higher density).

Current state of the affected surfaces (verified in source):

- `widgets/attention-hero/ui/AttentionHero.tsx:272` — `AllClearState` renders the `productivity-placeholder` block (testid `productivity-placeholder`) between the all-clear message and `ApprovalWaitSummary`.
- `pages/dashboard/ui/DashboardPage.tsx:74` — `dashboard-hero` renders `<AttentionHero />` plus an "Ask Agent" `<Button>` (testid `ask-agent-project`) that `navigate()`s to `/agent-sessions/new`. The button is the **sole** consumer of `useNavigate`, `useProjectPath`, `toProjectPath`, and `BotIcon` in this file.
- `widgets/dashboard-pulse/ui/PulseZone.tsx:42` — renders the `pulse-status-pills` block (testids `pulse-status-pills`, `pulse-pill-{active,waiting,completed,failed}`) from the `statusCounts` slice of `useActivityCards()`.
- `pages/dashboard/productivity/ProductivityZone.tsx:20` — mounts `<SnapshotRow />` as the first child; `SnapshotRow.tsx` consumes `useCompletionSnapshot` + `useIssues`.

Constraints:

- Single subsystem (`packages/web`, dashboard). No server / runner / CLI / API / database / schema changes; no new dependencies.
- The change is pure subtraction — no new components, routes, charts, query keys, or data contracts.
- Existing unit tests cover each affected component's render output and must be updated in lockstep so no residual assertion references a removed testid.
- `risk=low` per the issue: UI-only deletions, no schema/API migration.

Stakeholders: any user opening the Dashboard (information density / scan-ability); the dashboard web test suite (must stay green).

## Goals / Non-Goals

**Goals:**

- Remove the four identified redundancies/stale elements so the 首页 returns to a single 职责 of situational awareness + action.
- Tighten the `dashboard-attention`, `dashboard-pulse`, and `dashboard-shell` specs to make the removals anti-regression guards (placeholder exclusion, pill exclusion, Ask-Agent exclusion).
- Leave the affected web unit tests asserting the post-cleanup structure with no dead assertions on removed testids.
- `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` pass.

**Non-Goals:**

- No new Insights page/route (separate future issue).
- No new components, charts, or features.
- No changes to the remaining Productivity charts, Factory status headline, Digest widget, or Pulse session cards / overflow link.
- No changes to data contracts, API endpoints, or query keys.
- No restructure of the dashboard zone layout (still `pulse` / `productivity` / `digest` three-zone grid).
- No re-evaluation of *which* zone each signal belongs to beyond the four named deletions.

## Decisions

### Decision 1 — Four scoped deletions, mapped 1:1 to testids

Each acceptance criterion maps to a single render-site deletion plus its testid retirement. Treating them as independent edits keeps the diff reviewable and lets any single deletion be reverted without touching the others.

| # | Deletion | File (render site) | Retired testids |
|---|----------|--------------------|-----------------|
| 1 | All-clear placeholder block | `widgets/attention-hero/ui/AttentionHero.tsx` (`AllClearState`) | `productivity-placeholder` |
| 2 | Ask Agent button + its wrapper `<div>` | `pages/dashboard/ui/DashboardPage.tsx` (`dashboard-hero`) | `ask-agent-project` |
| 3 | Pulse status-pills block | `widgets/dashboard-pulse/ui/PulseZone.tsx` | `pulse-status-pills`, `pulse-pill-active`, `pulse-pill-waiting`, `pulse-pill-completed`, `pulse-pill-failed` |
| 4 | `<SnapshotRow />` mount (+ component + its test) | `pages/dashboard/productivity/ProductivityZone.tsx` + delete `SnapshotRow.tsx`, `SnapshotRow.test.tsx` | `productivity-snapshot-row`, `productivity-snapshot-empty`, `productivity-snapshot-completed`, `productivity-snapshot-failed`, `productivity-snapshot-new` |

**Alternatives considered:**

- *Replace rather than remove (e.g. repurpose the Ask Agent button, or merge the four pills into a single roll-up).* Rejected — the issue explicitly scopes this to subtraction; any new surface belongs in a follow-up so this change stays low-risk and reviewable.
- *Hide via CSS / feature flag instead of deleting.* Rejected — would leave the dead markup and its tests in the tree and contradict the "no stale content" goal. No users depend on these elements (internal DOM only).

### Decision 2 — Dead-code cleanup boundary: only provably-orphaned symbols

After each deletion, remove imports/symbols that become unreferenced **and only those**. Aggressive pruning risks deleting a shared export that still has other consumers; conservative pruning leaves lint warnings (`TreatWarningsAsErrors`-equivalent for TS / unused-var rules). Verified boundaries:

- **`DashboardPage.tsx`**: the Ask Agent button is the sole consumer of `useNavigate`, `useProjectPath`, `toProjectPath`, and `BotIcon` in the file (confirmed by grep). All four imports + the `navigate`/`toProjectPath` const declarations are removed. `Button` **stays** (still used by the empty-state "Create Project" button); `useState` stays (`showCreateProject`); `useProject` stays (`currentProject` drives `data-project`).
- **`PulseZone.tsx`**: drop `statusCounts` from the `useActivityCards()` destructure (keep `activeCards`, `slotUsage`), and delete the `PILL_STYLE` / `PILL_LABEL` maps. **Do not** touch `useActivityCards` itself or its `statusCounts` output — `ActivityPage.tsx` still consumes `statusCounts` (`src/pages/activity/ui/ActivityPage.tsx:39-42`). The shared model stays intact.
- **`AttentionHero.tsx`**: delete only the `productivity-placeholder` `<div>` inside `AllClearState`. The all-clear message, `ApprovalWaitSummary`, and the surrounding section markup stay. No imports become unused here.
- **`ProductivityZone.tsx`**: drop the `SnapshotRow` import and the `<SnapshotRow />` element. **Do not** delete `useCompletionSnapshot` / `completion-snapshot.ts` — it is a public export of `entities/issue` (`entities/issue/index.ts:16`) with its own test file (`completion-snapshot.test.ts`) and is independent of the dashboard. Delete only `SnapshotRow.tsx` and `SnapshotRow.test.tsx`.

**Alternatives considered:**

- *Also retire `useCompletionSnapshot` since its only UI consumer is SnapshotRow.* Rejected — it is a documented entity-level hook with its own unit tests and is a legitimate building block for any future surface; deleting it would expand the blast radius beyond "dashboard subtraction" and break `completion-snapshot.test.ts`.
- *Leave unused imports for a follow-up cleanup.* Rejected — the web build treats unused locals as errors and the change is small enough to clean up inline.

### Decision 3 — Test updates flip positive assertions to negative, then prune

For each affected spec, update in place rather than rewriting the file, so diff review shows exactly what flipped:

- **`AttentionHero.test.tsx`**: two tests currently assert the placeholder is *present* in All-clear — "renders the all-clear state with All clear message and Productivity placeholder…" (≈ line 596) and "renders the all-clear state when agentStatus is undefined…" (≈ line 636). Flip both to `expect(screen.queryByTestId('productivity-placeholder')).not.toBeInTheDocument()`. The existing negative assertions at ≈ line 188 and ≈ 565 (loading state) already match the new contract and stay.
- **`PulseZone.test.tsx`**: replace "renders the four status pills with counts sourced from the activity summary" (≈ line 102) with a test asserting `queryByTestId('pulse-status-pills')` and all four `pulse-pill-*` are absent, while `pulse-slots` still renders. Keep the capacity-header, empty-state, card-cap, and overflow-link tests unchanged.
- **`DashboardPage.test.tsx`**: delete the entire "DashboardPage Ask Agent entry (T-005)" `describe` block (≈ lines 314–355) and the now-orphaned `mockUseNavigate` hoist + the `useNavigate` override inside `vi.mock('react-router-dom', …)`. Add one assertion in the main describe that `dashboard-hero` contains no `ask-agent-project`. The `useActivityCards` mock can keep returning `statusCounts` (extra fields on a mock are harmless) — no change needed there.
- **`ProductivityZone.test.tsx`**: remove the `vi.mock('./SnapshotRow', …)` block (≈ lines 6–8) since the module no longer exists. No existing test in this file asserts `snapshot-row`, so no assertion flips are needed.

**Alternatives considered:**

- *Delete the whole pill test and the whole Ask-Agent describe without replacement.* Rejected — converting them to negative assertions preserves anti-regression coverage (a future change that re-adds a pill or the button would fail loudly), which is exactly what the spec deltas encode.
- *Add brand-new snapshot/a11y tests for the converged layout.* Rejected as out of scope; the existing per-component render tests already lock the structure.

### Decision 4 — Spec deltas encode the removals as SHALL-NOT anti-regression guards

The three spec files under `specs/` already express the deletion as requirements (All-clear SHALL NOT render placeholder; Pulse SHALL NOT render lifecycle pills; Dashboard hero SHALL NOT render Ask Agent). The implementation must satisfy these by construction (the markup is gone), and the negative test assertions from Decision 3 are the executable mirror. `SnapshotRow` needs no spec delta because no existing spec mandated a weekly-snapshot block — its removal is enforced purely by the web unit-test edits (per proposal note).

## Risks / Trade-offs

- **[Risk] DOM/testid breakage for any external consumer scraping these testids** → *Mitigation*: testids are internal-only (prefixed by feature, not part of any public API contract). The proposal declares the breakage explicitly. No public API or data-contract break.
- **[Risk] Visual layout shift in All-clear state** (removing the placeholder `<div>` collapses the spacing between the message and `ApprovalWaitSummary`) → *Mitigation*: the all-clear message already carries `mb-3`, and `ApprovalWaitSummary` renders its own `<p>`. Net change is intentional (less vertical chrome). Acceptable per the "single 职责" goal; no pixel-test exists to regress.
- **[Risk] Over-pruning a shared export** (e.g. deleting `statusCounts` from the model or `useCompletionSnapshot` from the entity barrel) → *Mitigation*: Decision 2 pins the cleanup boundary at "provably-orphaned in the edited file only"; shared exports with other consumers stay. Verified `ActivityPage.tsx` and `entities/issue/index.ts` as the retaining consumers.
- **[Risk] A future feature wants one of the removed surfaces back** (e.g. a richer Insights page wants the weekly snapshot) → *Trade-off*: accepted. Re-introduction is a net-new feature that will land through its own issue/spec; the git history retains the deleted components for reference. This change optimizes for current clarity, not speculative reuse.
- **[Risk] Negative test assertions are weaker than positive ones** (they only catch re-appearance of the exact testid, not a renamed re-introduction) → *Mitigation*: acceptable; the spec deltas carry the semantic intent, and renamed re-introduction would be a deliberate new feature reviewed on its own merits.

## Migration Plan

This is a pure UI deletion with no data, schema, API, or routing migration.

**Deploy:**

1. Land the four deletions + test updates in a single PR on `master` (no feature flag — the change is reversible by revert and has no user-data implications).
2. CI gates: `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` must be green. No server/runner/CLI build or test is affected.
3. No user communication required beyond the PR description (internal DOM-only change); no docs page references the removed placeholder copy or the Ask Agent button on the dashboard.

**Rollback:**

- Revert the commit. Because no data contract, query key, route, or persisted state is touched, revert is clean with no follow-up migration. The deleted files (`SnapshotRow.tsx`, `SnapshotRow.test.tsx`) are restored from git.

**Ordering within the PR:** deletions can be applied in any order (they are independent render sites), but apply each component's source edit and its test edit in the same commit so the test suite is never transiently red on `master`.

## Open Questions

- None blocking. The cleanup boundary (Decision 2) is the only judgment call and is resolved by the grep-verified consumer check. If review prefers to *also* retire `useCompletionSnapshot` (currently kept for being a public entity export with its own tests), that is a one-line scope expansion — flag it rather than decide unilaterally.
