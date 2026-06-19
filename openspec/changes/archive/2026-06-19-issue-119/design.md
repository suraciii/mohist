## Context

Settings is a single `SettingsPage` (`packages/web/src/pages/settings/ui/SettingsPage.tsx`) built on Radix Tabs, URL-driven via `/settings/:section`. It renders one of 6 independent section components selected from a `VALID_SECTIONS` tuple and a `SECTION_META` array. Relevant current state:

- **Field metadata is partially centralized already.** `AgentSettingsSection.tsx` keeps a `FIELDS: FieldDef[]` array (`{ key, label, unit, description, group }`) and renders stable ids `agent-runtime-<key>`. `AiSettingsSection.tsx` exposes stage labels with ids (`settings-stage-model-<stage>-label`, `settings-default-model-label`). Other sections (Repositories, Workflows, Templates, System) are ad-hoc and most controls lack a stable focusable id.
- **cmdk is already present.** `packages/web/src/shared/ui/components/command.tsx` exports the full primitive set (`Command`, `CommandDialog`, `CommandInput`, `CommandList`, `CommandEmpty`, `CommandGroup`, `CommandItem`) on top of the `cmdk` package + Radix Dialog.
- **Dark mode is class-based but unactivated.** `src/app/styles/index.css` declares `@custom-variant dark (&:is(.dark *))` and a full `.dark` token block; components ship `dark:` variants that never trigger because nothing ever adds the `.dark` class. `index.html` has no pre-paint script, so applying the theme only from React would flash light-then-dark (FOUC).
- **The sidebar ⌘B shortcut already exists** (`shared/ui/components/sidebar.tsx`, `metaKey||ctrlKey` + `b`). So the Preferences shortcut reference can list it truthfully.
- **Tests.** Vitest unit tests live under `packages/web/tests/` and colocated `*.test.tsx`. Playwright exists **only** for a11y (`tests/a11y/settings.a11y.spec.ts` + `playwright.a11y.config.ts`, `testDir: ./tests/a11y`, preview server on 4173). There is no non-a11y Playwright config yet.
- **localStorage convention.** Existing key `mohist:settings-onboarding-dismissed` → theme key will be `mohist:theme`.

Constraints: no backend changes this issue (deferred to a `/api/preferences` follow-up); ⌘K stays Settings-page-scoped (must not claim a global slot); pure frontend SPA on Vite + React Router + TanStack Query.

## Goals / Non-Goals

**Goals:**
- Let users find any Settings field by typing, without flipping tabs, via ⌘K/Ctrl+K scoped to the Settings page.
- Backfill a central, static registry of field descriptors (tab, label, description, placeholder, stable focus-target id) so search + Enter-to-focus work across all tabs.
- Add a 7th Preferences tab with a real light/dark/system theme selector (instant, persisted to `localStorage`, no FOUC, system default) and a truthful read-only shortcut reference.

**Non-Goals:**
- Global command palette; backend theme persistence / cross-device sync (`/api/preferences`); notification preferences; timezone/CLI path (stay in System tab); fixing pre-existing tinted-`CardSection` dark-mode contrast debt beyond what the (now dark-scanned) a11y regression surfaces.

## Decisions

### D1 — ⌘K/Ctrl+K is bound locally inside `SettingsPage`, not globally
A `SettingsSearch` component mounted within `SettingsPage` registers a `keydown` listener in a `useEffect` and cleans it up on unmount. The handler ignores the keystroke when the active element is an editable control or the dialog is already open. Routing is untouched.
- *Alternative considered:* a global app-level handler that checks the current route. **Rejected** — it would occupy the global ⌘K slot (explicitly forbidden) and complicate future global-palette work.
- *Alternative considered:* exposing a visible "Search" button. **Rejected** by scope (the issue specifies keyboard invocation).

### D2 — A single static registry aggregates descriptors exported by each section
A new `settings-search/registry.ts` imports descriptor arrays exported by each section component and concatenates them. Descriptor shape: `{ tab: Section, label, description, placeholder?, focusTargetId }`. Sections that already hold this metadata (`AgentSettingsSection.FIELDS`, `AiSettingsSection` stages) export it in this shape; sections that don't (Repositories/Workflows/Templates/System) get a small backfill of both the stable focusable id on each control and a descriptor entry.
- *Alternative considered:* runtime registration via a `useRegisterSettingsFields` hook called inside each section. **Rejected** — descriptors are static metadata (label/description/placeholder), independent of server data; runtime registration adds lifecycle/ordering complexity and depends on tab mount order. Static aggregation is simpler to test and grep.
- Focus resolution is **not** stored in the registry. The registry stores only the `focusTargetId`; the element is resolved via `document.getElementById` at Enter time, after the target tab mounts.

### D3 — Filtering excludes current values by setting cmdk `value` explicitly
Each `CommandItem` sets `value={`${label} ${description} ${placeholder ?? ''}`}` (lower-cased) and renders label + owning-tab as children. cmdk filters on the `value` prop, so live numeric values are never searchable. This satisfies "search 30 must not match every timeout".
- *Alternative considered:* post-filtering the cmdk results in JS. **Rejected** — fights cmdk's built-in ranking; controlling `value` is the idiomatic, one-line solution.

### D4 — Enter navigates via the existing URL mechanism, then focuses via element-poll
On Enter: close dialog → `navigate(toProjectPath('/settings/<tab>'))` (reuses the existing tab-routing) → poll `document.getElementById(focusTargetId)` on each `requestAnimationFrame` up to ~500ms, then `.focus()` and `scrollIntoView`. Radix Tabs unmounts inactive `TabsContent`, so the target mounts only after the route change; the poll absorbs that timing.
- *Alternative considered:* thread a `pendingFocusId` through `SettingsPage` state and have the target tab focus itself in a mount `useEffect`. Cleaner React but invasive (touches every section's props); deferred unless the poll proves flaky in e2e.

### D5 — Theme = class-based `.dark` toggle on `<html>` + inline pre-paint script + a `ThemeProvider`
- **No FOUC:** add a tiny synchronous `<script>` in `index.html` `<head>` (before paint) that reads `mohist:theme`, resolves `system` via `matchMedia('(prefers-color-scheme: dark)')`, and adds/removes `.dark` on `document.documentElement`. `localStorage` access is wrapped in `try/catch` (private mode). The resolution logic is extracted into a pure `resolveTheme(stored, prefersDark): 'light'|'dark'` function so it is unit-testable and shared between the script and React.
- **Runtime:** a minimal `ThemeProvider` mounted at the App root exposes `{ theme: 'light'|'dark'|'system', setTheme }`, toggles `.dark`, persists to `mohist:theme`, and subscribes to `matchMedia` changes while in `system` mode (so OS toggles update live).
- *Alternative considered:* apply the theme only inside a React `useEffect`. **Rejected** — guarantees a one-frame light flash (FOUC) for dark/system users.
- *Alternative considered:* adopt `next-themes`. **Rejected** — pulls a Next-oriented dependency into a Vite SPA for ~30 lines we can own.

### D6 — Preferences tab composes the existing shared wrappers
`PreferencesSection` renders its page title through the existing `<SettingsSection>` and its cards through `<CardSection tone="default">`, matching the other tabs (satisfies the modified `settings-visual-consistency` scope). The theme control is a labeled 3-option segmented control / Radix RadioGroup (`aria-labelledby`/`<label htmlFor>`). The shortcut reference is a read-only list inside a `CardSection`.

### D7 — Shortcut reference has one source of truth
A `keyboard-shortcuts.ts` constant array (`{ id, label, keys }`) lists only shortcuts that actually exist today (`sidebar-toggle` ⌘B, `settings-search` ⌘K). Preferences renders from this array. A unit test asserts each entry's id maps to a registered handler, preventing drift toward "fake" shortcuts. No minimum-count target.

## Risks / Trade-offs

- **[Tinted `CardSection` tones have no `dark:` variants → poor dark contrast]** → Mitigation: Preferences uses `tone="default"` only; the broader debt is left to the a11y regression (now scanned in dark per the modified `settings-accessibility` spec) to surface violations elsewhere, tracked as a follow-up rather than fixed inline.
- **[Focus-after-navigation timing: target tab content mounts asynchronously → focus may silently no-op]** → Mitigation: rAF-poll with timeout (D4); e2e asserts the field actually receives focus, so a regression fails the build.
- **[cmdk could match rendered children text if a `value` is omitted]** → Mitigation: every `CommandItem` sets an explicit `value` (D3); a unit test seeds a numeric value and asserts it is not matched.
- **[Inline pre-paint script runs before React; a bug there is hard to debug]** → Mitigation: keep the script to a call into the pure `resolveTheme` (unit-tested); the script itself only does classList + try/catch.
- **[⌘K collides with future global palette or with typing ⌘K inside the search input]** → Mitigation: handler is local to `SettingsPage` (D1) and self-guards when the dialog is open or focus is in an editable element.
- **[`SettingsSection` renders `<h2>` today while the visual-consistency spec says page titles are `<h3>`]** → Out of scope to fix here; Preferences reuses the existing wrapper as-is for consistency with the other 6 tabs. Flagged in Open Questions.

## Migration Plan

Frontend-only; no backend, DB, or API changes.

- **Deploy:** build & ship `packages/web`. The `.dark` activation and the new `mohist:theme` key are purely additive; users without a stored preference stay on the current light theme.
- **Verify:** `npm run test` (vitest: updated `SettingsPage.test.tsx`, new `PreferencesSection`/search/theme tests), `npm run test:e2e` (new Playwright config), and the extended a11y spec (`tests/a11y/settings.a11y.spec.ts`) now covering the Preferences tab + open search dialog in both light and dark.
- **Rollback:** revert the commit. localStorage key is orphaned but harmless; UI returns to the prior light-only behavior.

## Open Questions

1. Should the ⌘K search also surface the new Preferences fields (e.g. the "Theme" control), or stay limited to the original 6 config tabs? (Spec currently says "all Settings tabs"; leaning toward including Preferences' controllable fields.)
2. Pre-existing `SettingsSection` renders `<h2>` while `settings-visual-consistency` requires page titles to be `<h3>` — fix as part of this issue, or leave and track separately?
3. Confirm the keyboard-shortcut reference should list both Mac (⌘) and Windows (Ctrl) glyphs, or platform-detect at render time.
