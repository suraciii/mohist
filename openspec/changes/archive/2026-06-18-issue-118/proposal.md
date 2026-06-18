## Why

The Settings surface (6 tabs) was built for desktop mouse use and never received a coordinated accessibility + responsiveness pass. On mobile/narrow screens, touch targets fall below the 44×44px minimum (e.g. `RepositoriesSection` action buttons use `h-7`); on keyboard and screen readers, mutation feedback is silent (toasts lack `aria-live`), focus order/traps and heading hierarchy are unverified, and several tokens (`text-muted-foreground` on `bg-muted/40`, error text on `bg-red-50`) are suspected sub-4.5:1 contrast. Users on phones or assistive tech cannot reliably operate or read Settings today. This is one coordinated pass because all four problem classes share the same files (`SettingsPage.tsx` + 6 `*Section.tsx` + `SettingsSection.tsx`) and the same verification tool (axe-core).

## What Changes

- Enforce ≥44×44px touch targets (incl. padding) for every interactive element across the 6 Settings tabs; remove sub-minimum sizing like `h-7` from `Set default` / `Remove` buttons
- Verify and fix keyboard navigation across all tabs: Tab reaches every interactive element in DOM order with no traps; Enter/Space activates buttons (incl. Stage Model Overrides disclosure); Escape closes dialogs/popovers; arrow keys operate Select/ModelSelect; `:focus-visible` ring visible in light and dark themes
- Verify and fix WCAG AA contrast at runtime via axe-core across all 6 tabs; specifically `SettingsSection` description on muted backgrounds and error-state text on tinted backgrounds
- Add screen-reader semantics: monotone heading hierarchy (page h1 → Section h3 → subtitle h4, no skips); `<label>`/`aria-labelledby` on every form input; `aria-live` region for mutation success/failure (or toast with `role="status"`); `aria-expanded`/`aria-modal` on folding/dialog state; focus trap + `aria-labelledby` on any modal dialog (the Template Editor is currently an inline panel, not a modal — confirmed by audit)
- Add axe-core accessibility regression coverage for all 6 Settings tabs (integrated into the existing test suite or Playwright)
- Preserve all existing functional behavior, API contracts, and persisted data — this is a presentation/a11y-only pass

## Capabilities

### New Capabilities

- `settings-accessibility`: Settings surface (6 tabs) accessibility and responsive behavior — touch-target sizing, keyboard navigation, screen-reader semantics (`aria-live`, labels, focus trap, heading hierarchy), WCAG AA contrast runtime enforcement via axe-core, and a11y regression coverage scoped to `/settings/*`. Complements the contrast *principle* already established in `settings-visual-consistency` by adding the runtime *enforcement* and the broader a11y dimensions.

### Modified Capabilities

_None._ The existing WCAG AA contrast requirement in `settings-visual-consistency` already states the principle ("all Settings body text SHALL pass"); this change enforces it at runtime rather than altering the requirement. All other dimensions (touch targets, keyboard, screen-reader semantics, regression tooling) are net-new spec-level behavior covered by the new `settings-accessibility` capability.

## Impact

- **Frontend code**: `packages/web/src/pages/settings/ui/` — `SettingsPage.tsx`, `SettingsSection.tsx`, all 6 `*Section.tsx` files, `TemplateEditor.tsx`, `ModelSelect`. Primarily className/ARIA-attribute changes; no business-logic or state-semantics changes.
- **Shared UI consumption**: Toast, Dialog, Select from `@/shared/ui/components` are *consumed* in Settings (not refactored). Per Non-Goals, if a shared component lacks an a11y attribute, a minimal patch or Settings-level workaround is used; a shared-component refactor would be a separate issue.
- **Tests**: `SettingsPage.test.tsx` and section unit tests must keep passing unchanged in intent; new axe-core a11y tests added for all 6 tabs (vitest integration or Playwright).
- **No backend/API impact**: `/api/config`, `/api/agent-runtime`, `/api/system/info` contracts unchanged; no migrations, no new endpoints.
- **No new dependencies**: axe-core is the verification tool (no component-library replacement; shadcn/Radix/Base UI retained).
- **Risk (medium)**: cross-Section className/token/ARIA changes (e.g. touch-target sizing, `aria-live` regions) render across all tabs simultaneously since the 6 sections share the `SettingsSection` shell. Contained to the Settings surface — no backend契约 or migration risk, hence not high.
