## Context

The Settings surface (`packages/web/src/pages/settings/ui/`) renders 6 tabs (`ai`, `agent`, `repositories`, `workflows`, `templates`, `system`) inside a shared `SettingsPage` → `Tabs` shell. Each tab is a `*Section.tsx` that wraps content in the shared `SettingsSection` component (which renders the title as `<h3>`). Interactive primitives come from `@/shared/ui/components/*`, which are **Base UI** (`@base-ui/react ^1.5.0`) wrappers. Mutation feedback goes through **sonner** (`Toaster` mounted once in `app/App.tsx:73`); toast calls live in `entities/settings/api/queries.ts`.

**Current test infra**: `vitest` + `jsdom` + `@testing-library/react`. There is **no axe-core dependency and no Playwright/e2e harness** anywhere in the repo.

The issue explicitly mandates an **audit-first** workflow (axe-core + keyboard + screen-reader sweep *before* edits), because its original "potential problem checklist" was written from static guessing and the first code pass already disproved several items. Code inspection during this design confirmed more already-correct assumptions and pinpointed the *real* gaps:

**Already correct (verify, do not "fix")**:
- Responsive layouts: `AgentSettingsSection` grid (`grid-cols-1 sm:grid-cols-3`), `RepositoriesSection` list (`space-y-2` single-column), Git URL rows (`min-w-0` + `truncate`) — all already responsive.
- Mutation toast accessibility: sonner renders toasts with `role="status"` and an `aria-live` region by default → mutation success/error IS announced. (Note: `RuntimeToastHost` is a *separate* system for connection/transport toasts and already sets `role="alert"`/`role="status"` — not the path settings mutations use.)
- Base UI `Dialog`/`Popover` provide `aria-modal`, focus trap, and Escape handling natively.
- **`TemplateEditor` is NOT a modal dialog** — it is an inline `CardSection` panel (`TemplateEditor.tsx:204`). The issue's AC for "Template Editor dialog focus trap / `aria-labelledby`" therefore does not apply as written; there is no dialog to trap. This is a false-premise item the audit must reclassify.

**Real gaps confirmed by code inspection**:
- `RepositoriesSection.tsx:95,106` — `Set default` / `Remove` buttons use `className="text-xs h-7"` → 28px, below the 44px touch target.
- `AiSettingsSection.tsx:88-98` — the "Stage Model Overrides" disclosure `<Button>` toggles `stageOverridesOpen` but exposes **no `aria-expanded`** and no `aria-controls`; the chevron rotation is visual-only, invisible to screen readers.
- `AiSettingsSection.tsx:71,104` — `<label>` elements for the ModelSelect controls have **no `htmlFor`**, and `ModelSelect` exposes no `id`; labels are orphaned and not programmatically associated.
- `TemplateEditor.tsx:214` — close button uses `size="icon-xs"` (sub-44px touch target).
- **No page-level `<h1>` anywhere** under `settings/ui/`; the top heading is `SettingsSection`'s `<h3>`, with `<h4>` subtitles below. See Open Questions for the resulting heading-order tension.

## Goals / Non-Goals

**Goals:**
- Deliver one coordinated a11y + responsiveness pass over the 6 Settings tabs that satisfies the `settings-accessibility` spec.
- Run a real axe-core + keyboard audit first, then fix only the confirmed gaps (do not edit already-correct code).
- Add automated a11y regression coverage that survives after this issue.

**Non-Goals** (from the issue):
- Settings-external pages (Board / Activity / Issue detail / Session).
- #119 Preferences Tab / search.
- Site-wide a11y design-system or CI lint infra.
- Replacing shadcn / Base UI / sonner.
- Backend API or persisted-data changes.
- Refactoring the shared toast / Dialog / Select component *internals* (consume them; minimal attribute patch only if a real gap is found).
- i18n of error/aria text.

## Decisions

### 1. Execute audit-first; track a confirmed-defect list before any edit
**Decision**: Before touching code, run (a) an axe-core scan over each of the 6 tabs and (b) a manual keyboard sweep, and record the confirmed-defect list. Only defects on that list get fixed.
**Rationale**: The issue's Domain Model already disproved several原预测, and this design pass disproved more (TemplateEditor is not a modal; sonner toast is already announced). Editing from the original guessed checklist would corrupt working code.
**Alternatives**: Edit directly from the issue's AC list — rejected for the reason above.

### 2. Two-track automated a11y coverage: jsdom structural rules + real-browser contrast
**Decision**:
- Add `axe-core` + `vitest-axe` (jest-axe-compatible) for the **structural** rules that run reliably under jsdom (`aria-*`, `heading-order`, `label`, `button-name`, `tabindex`, `aria-expanded`, `aria-allowed-attr`). These execute inside the existing vitest suite per tab.
- Add a **scoped Playwright + `@axe-core/playwright`** suite that loads `/settings/<tab>` in a real browser and asserts **`color-contrast`** (plus a full re-run as a safety net) across all 6 tabs.

**Rationale**: jsdom does not compute layout or resolved colors, so axe-core's `color-contrast` rule is unreliable/skipped under vitest. The issue explicitly requires WCAG AA contrast verification, which can only be honestly automated in a real browser. Structural rules, by contrast, are fast and deterministic in jsdom and belong next to the existing component tests.

**Alternatives considered**:
- *Token-static guard script* (map Tailwind tokens → hex, compute ratios without a browser). Cheaper, but cannot catch context-dependent contrast (e.g. `text-muted-foreground` on `bg-muted/40`) and would duplicate `settings-visual-consistency`'s existing grep guard. Kept as a fallback if Playwright proves too heavy.
- *Full Playwright for everything*. Slower feedback than vitest for structural rules; rejected for the structural track.
- *Manual-only contrast*. Rejected — not regression-safe.

### 3. Touch targets via a Settings-local hit-area utility, not a shared-component edit
**Decision**: Introduce a small Settings-local className convention (`min-h-11 min-w-11` = 44px, applied via padding so the visual control may stay compact) for sub-target controls (`RepositoriesSection` action buttons, `TemplateEditor` icon close, icon-only buttons). Do **not** edit the shared `Button` component.
**Rationale**: Honors the Non-Goal of not refactoring shared components; confines the change to Settings files; keeps dense rows (Repositories list) visually compact while extending the hit area.
**Alternatives**: Promote controls to Base UI `size="default"` (40px — still under 44); add a new shared `Button` size variant (shared-component change — out of scope). Both rejected.

### 4. Heading hierarchy: add a page `<h1>`; resolve the h1→h3 skip during the audit
**Decision**: Add exactly one page-level `<h1>` ("Settings") to `SettingsPage.tsx` (visually it can be the existing tab-area heading or an `sr-only` landmark). The audit then determines whether the `SettingsSection` `<h3>` must demote to `<h2>` to pass axe-core `heading-order`.
**Rationale**: There is currently no `<h1>`; the surface jumps straight to `<h3>`. Adding `<h1>` is unambiguously required. But `h1 → h3` skips `<h2>` and axe `heading-order` flags jumps > 1 — so the audit's verdict dictates whether `SettingsSection` becomes `<h2>`.
**Alternatives**: Insert an `sr-only` `<h2>` bridge (hacky, non-semantic); accept the `serious` heading-order violation (fails the AC "critical+serious = 0"). Both rejected unless the audit shows a real-world screen-reader problem with the demotion.

> ⚠ This decision may force a **MODIFIED** delta on `settings-visual-consistency` (whose requirement pins `SettingsSection` to `<h3>`). If the audit mandates `<h2>`, the proposal's "Modified Capabilities: None" stance must be reopened. See Open Questions.

### 5. Associate orphan labels via `aria-labelledby` on the ModelSelect wrapper
**Decision**: For `AiSettingsSection`'s ModelSelect controls, give each `<label>` an `id` and pass `aria-labelledby` through `ModelSelect` to its trigger button. This associates the existing visible label with the control without a shared-component refactor (only requires `ModelSelect` to accept/forward an `aria-labelledby` prop — a minimal additive change, not an internal refactor).
**Rationale**: `htmlFor`/`id` pairing is awkward for a composite control whose active element is a button; `aria-labelledby` is the idiomatic ARIA pattern and requires only prop forwarding.
**Alternatives**: Generate an `id` inside `ModelSelect` and require callers to pass it to `<label htmlFor>` — more boilerplate per call site. Rejected.

### 6. Add `aria-expanded` + `aria-controls` to the Stage Model Overrides disclosure
**Decision**: On `AiSettingsSection.tsx:88`, add `aria-expanded={stageOverridesOpen}` and `aria-controls` pointing at the collapsible region's `id`.
**Rationale**: The expansion state is currently visual-only. This is a pure additive attribute change, no logic change.

### 7. Verify — do not modify — sonner, Base UI Dialog, and Base UI Popover
**Decision**: Add tests asserting that (a) sonner toasts carry `role="status"`/`aria-live`, (b) any *actual* modal Dialog usage exposes `aria-modal` + focus trap, (c) ModelSelect Popover moves focus to the search input on open. No code change to those shared components is planned.
**Rationale**: Code inspection indicates these are already accessible; the audit confirms it. This honors the Non-Goal and avoids speculative shared-component edits. (Note again: `TemplateEditor` is inline, not a Dialog, so its "dialog" AC is reclassified as non-applicable pending audit.)

## Risks / Trade-offs

- **[jsdom cannot compute color-contrast]** → Mitigation: real-browser Playwright track for `color-contrast`; structural rules stay in fast vitest. (Decision 2.)
- **[Heading-order fix may force a `settings-visual-consistency` spec amendment]** → Mitigation: defer to audit verdict; if `<h2>` is required, reopen the proposal's "Modified: None" and add the delta before archive. (Decision 4, Open Questions.)
- **[Introducing Playwright is new repo infra]** → Mitigation: scope the suite strictly to the 6 Settings routes; reuse the existing dev server / build; keep it as a separate `test:a11y` script so it cannot block the main vitest run. If the team rejects the infra add, fall back to the token-static guard (Decision 2 alternative).
- **[Touch-target `min-h-11` may visually inflate dense rows]** → Mitigation: apply via padding extension, keep the control's visual box compact; visually verify Repositories list on mobile after change.
- **[Base UI / sonner version assumptions (`@base-ui/react ^1.5.0`, `sonner ^2.0.7`)]** → Mitigation: the audit's first run confirms the a11y attributes are actually emitted at the installed version before we rely on them.
- **[False-premise ACs (TemplateEditor "dialog")]** → Mitigation: the audit-first list is the source of truth; ACs that map to non-existent UI (no modal) are reclassified rather than forced.

## Migration Plan

This is a **frontend-only, additive** change (className/ARIA attributes + heading elements + new tests). There are:
- **No database migrations, no API contract changes, no persisted-data shape changes.**
- **No feature flag required** — ARIA attributes and heading-level changes are safe and additive; they cannot break existing functional flows.
- **Rollback**: revert the change commit / PR. No state to restore.

Deployment is the normal web build (`pnpm --filter web build`). The new a11y test scripts (`test:a11y` for Playwright, extended `test` for vitest-axe) should run in CI alongside existing tests.

## Open Questions

1. **Heading level of `SettingsSection` (h2 vs h3)?** If axe `heading-order` flags the new `<h1>` → `<h3>` jump, `SettingsSection` must become `<h2>`, which amends the `settings-visual-consistency` requirement and reopens the proposal's "Modified Capabilities: None". Needs the audit's verdict, then possibly a spec delta.
2. **Playwright or token-static guard for contrast?** Decision 2 recommends Playwright, but the repo has zero e2e infra today. Team sign-off on adding Playwright as a devDep + a `test:a11y` job is required; otherwise fall back to the lighter token-static script.
3. **Does `ModelSelect` forwarding `aria-labelledby` count as an allowed "minimal patch" or a shared-component change?** Decision 5 treats prop forwarding as additive and in-scope; confirm this is acceptable under the Non-Goals (the alternative is a Settings-local wrapper component, more code).
4. **Is a visible or `sr-only` page `<h1>` preferred?** A visible "Settings" page title changes the layout slightly; `sr-only` satisfies the landmark with no visual change. Product/team preference.
5. **Reclassify the TemplateEditor "dialog focus trap" AC?** Confirmed it is an inline panel, not a modal. Should the spec's Template-Editor-traps-focus scenario be dropped/rewritten, or should TemplateEditor actually become a modal (out of scope for this issue)? Pending audit + product confirmation.
