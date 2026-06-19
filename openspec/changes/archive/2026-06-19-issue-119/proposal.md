## Why

Settings has grown to 6 tabs and 20+ fields with no search and no theme control. Users cannot locate a specific setting (e.g. a build-stage model or a timeout) without flipping between tabs, and long sessions are locked to light mode — the `dark:` styles already written into components are currently dead CSS with no activation mechanism. Settings-scoped search and a Preferences tab directly remove these two gaps.

## What Changes

- Add **settings-scoped search**: ⌘K (Mac) / Ctrl+K (Windows) opens a command palette **only inside the Settings page** (it does NOT claim the global ⌘K slot). It reuses the existing cmdk primitives (`Command` / `CommandDialog` / `CommandInput` / `CommandList` / `CommandEmpty` / `CommandGroup` / `CommandItem`) — no new command-palette infrastructure.
- Establish a **central searchable settings registry**: every field exposes a descriptor (owning tab, label, description, placeholder, stable focus-target id). The 6 current sections are independent components with no central registry and most fields lack a stable focusable id — backfilling these ids is the core work of this change.
- Search filters on **label / description / placeholder only — not current values** (numeric values like "30" would noise-match every timeout field). Enter jumps to the field's tab and focuses it; Esc closes; empty result shows "No matching settings".
- Add a 7th **Preferences** tab holding only real, controllable user preferences and read-only reference info (no fake controls, no system-fact items):
  - **Theme**: light / dark / system three-way selector; switch takes effect immediately; persisted to `localStorage` (this issue's scope); no theme flash on load (no FOUC); defaults to the system `prefers-color-scheme`.
  - **Keyboard-shortcut reference** (read-only): lists only currently-real shortcuts (sidebar toggle ⌘B, settings search ⌘K). No hard minimum-count target.

**Explicit non-goals** (deferred): notification preferences (no notification subsystem yet); timezone / CLI path (system facts, stay in System tab); theme backend persistence / cross-device sync (`/api/preferences`, extends the server `User` domain); a global command palette (⌘K stays settings-page-scoped).

## Capabilities

### New Capabilities

- `settings-search`: Settings-page-scoped search invoked by ⌘K/Ctrl+K; a central registry of searchable field descriptors (owning tab, label, description, placeholder, stable focus-target id); filtering on label/description/placeholder while **excluding values**; Enter-to-focus navigation to the owning tab and field; "No matching settings" empty state; built on existing cmdk primitives with no new command-palette infrastructure.
- `settings-preferences`: New 7th Preferences tab. A light/dark/system theme selector that applies instantly, persists to `localStorage`, defaults to the system `prefers-color-scheme`, and loads with no FOUC; plus a read-only keyboard-shortcut reference listing only currently-real shortcuts.

### Modified Capabilities

- `settings-accessibility`: Scope expands from "the 6 Settings tabs" to the 7th Preferences tab and the new settings search dialog. The search dialog must satisfy the existing modal-dialog requirement (focus trap while open, `aria-modal`, `aria-labelledby`, Escape to close); theme-selector and shortcut-reference interactive elements must meet the 44×44 touch-target and `:focus-visible` requirements. The axe-core regression scenario's tab enumeration must include Preferences.
- `settings-visual-consistency`: Scope expands from "all 6 tabs" to "all 7 tabs". The Preferences tab's card containers and page title must use the shared `CardSection` and `SettingsSection` components like the other tabs, rather than hand-rolled wrappers.

## Impact

- **Frontend — settings surface**: `SettingsPage` gains a 7th tab and a settings-scoped ⌘K handler; a new `PreferencesSection` component; a new settings-search module (registry + cmdk-based dialog); each existing section backfills stable focusable ids on its fields.
- **Theme system**: Activate the currently-dead `dark:` styles via a `class`/`data-theme` strategy on the root element; an inline pre-paint script (or equivalent) applies the persisted/system theme before render to eliminate FOUC.
- **Tests**: Update `SettingsPage` unit tests and per-section unit tests for the new tab; add search unit tests plus a new non-a11y Playwright config (the repo currently only ships `playwright.a11y.config.ts`) for search e2e.
- **localStorage**: New keys for the theme preference (alongside the existing onboarding-banner key).
- **No backend changes** in this issue; `/api/preferences` backend persistence is deferred to a follow-up issue.
