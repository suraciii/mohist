## Why

Settings is mounted entirely under `/:projectName/settings/:section` (`App.tsx:61,75`), so it is wrapped by `ProjectGuard` + `ProjectRouteScope`. But 4 of the 9 tabs — Coder Agent, Runtime, System, Preferences — are *application-level* config, not project config. The consequences: when no project exists the whole settings surface is unreachable (the project-existence guard blocks it); switching project silently changes half the tabs' content while leaving the other half unchanged with no visual cue; and a user configuring the Coder Agent model at `/:projectName/settings/ai` reasonably (but wrongly) believes the choice is scoped to that project. Separately, the dismissible onboarding banner on the Coder Agent tab is noise that has outlived its purpose. This must be fixed now because the tab count has reached 9 and is growing, and the top horizontal tab bar no longer scales.

## What Changes

- **Split settings routing by scope.** Application-level tabs (Coder Agent, Runtime, System, Preferences) move to `/settings/*`, outside `ProjectRouteScope` and no longer blocked by `ProjectGuard`. Project-level tabs (Repositories, Templates, Label catalog, Workflows, Inbox) stay under `/:projectName/settings/*`.
- **Replace the horizontal tab bar with a left sub-navigation** grouped into **Application** (Coder Agent, Runtime, System, Preferences) and **Project** (Repositories, Templates, Label catalog, Workflows, Inbox) sections, so the scope of each setting is visually distinguishable.
- **Align section headings with their nav labels**, moving clarification copy into each section's description.
- **Keep deep links working.** Existing `/:projectName/settings/<global-section>` deep links resolve via redirect to `/settings/<global-section>` so nothing breaks.
- **Reachability without a project.** When the system has no project, the global settings tabs remain accessible.
- **Narrow-view affordance.** The sub-navigation surfaces a visual cue (gradient/arrow/"more") when it overflows instead of silently clipping.
- **Keyboard accessibility.** The sub-navigation supports arrow-key navigation with roving tabindex and sets `aria-current="page"` on the active item.
- **Remove onboarding.** Delete `OnboardingBanner.tsx` and its test; remove `showOnboarding` state, the `ONBOARDING_DISMISSED_KEY` localStorage logic, and the onboarding render branch from `SettingsPage.tsx`. No code or reference to `OnboardingBanner` remains.

## Capabilities

### New Capabilities

- `settings-shell`: The Settings shell information architecture — routing scope (application-level tabs at `/settings/*` outside the project scope and guard, project-level tabs at `/:projectName/settings/*`), the grouped left sub-navigation distinguishing Application vs Project, section-heading/nav-label alignment, deep-link redirect preservation, no-project reachability of global settings, overflow affordance, keyboard navigation (arrow keys + roving tabindex + `aria-current="page"`), and the removal of the onboarding banner surface.

### Modified Capabilities

None. Existing requirements in `web-ui` that reference Settings sections (e.g. workflow-profile stage rendering, Settings Search discoverability) describe section *content* and *discoverability*, not the shell routing or navigation layout; those behaviors are preserved (Settings Search still navigates to the target section, now under its correct scope).

## Impact

- **Web** (`packages/web`):
  - `app/App.tsx` — add `/settings/*` routes outside `ProjectGuard`/`ProjectRouteScope`; keep `/:projectName/settings/*` for project-scoped tabs; add redirect from legacy global-section project URLs.
  - `widgets/app-shell/ui/ProjectGuard.tsx` — extend the existing `/settings` bypass so the full `/settings/*` tree is not blocked (the project-existence "No projects yet" gate no longer applies to global settings).
  - `pages/settings/ui/SettingsPage.tsx` — replace the `Tabs`/`TabsList` horizontal bar with a left sub-navigation grouped Application/Project; scope-aware navigation (global sections route to `/settings/*`, project sections to `/:projectName/settings/*`); remove `showOnboarding`, `ONBOARDING_DISMISSED_KEY`, and the onboarding render branch.
  - `pages/settings/ui/OnboardingBanner.tsx` (+ its test) — **deleted**.
  - `features/settings-search/SettingsSearch.tsx` — selection navigates to the entry's tab under its correct scope (global vs project) instead of always via `toProjectPath`.
  - Tests: `SettingsPage.test.tsx`, `SettingsSearch.test.tsx`, `Header.test.tsx` updated for the new routing/scope; remove onboarding assertions.
- **Server / runner / CLI**: none. No HTTP API, domain, or persistence change — this is a Web shell/IA refactor only.
- **Risk** (medium): changes application routing and the settings shell layout; the deep-link redirect and existing `Settings Search` navigation paths must be preserved to avoid breaking bookmarks and in-app jumps.
