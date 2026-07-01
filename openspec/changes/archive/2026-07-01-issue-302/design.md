## Context

Settings is a single page mounted entirely under `/:projectName/settings/:section` (`App.tsx:61,75`), wrapped by `ProjectGuard` + `ProjectRouteScope`. Of its 9 sections, 4 — Coder Agent (`ai`), Runtime (`agent`), System (`system`), Preferences (`preferences`) — are *application-level* config; the other 5 — Repositories, Templates, Label catalog, Workflows, Inbox — are *project-level*.

Current consequences:

- The project-existence gate in `ProjectGuard` (`ProjectGuard.tsx:19`) only does an **exact** match on `'/settings'`, so `/settings/ai` is **not** bypassed — every real settings URL is blocked when no project exists.
- The sidebar Settings entry is hidden entirely when `currentProject === null` (`AppSidebar.tsx:275`), so global config is unreachable with zero projects.
- Navigation is built with Radix `Tabs` (horizontal `TabsList`, `SettingsPage.tsx:124-146`) and `useProjectPath`, which **always** prepends the project name. So selecting any tab silently rewrites the URL into the project scope, even for app-level config.
- A dismissible `OnboardingBanner` (`OnboardingBanner.tsx`) plus `ONBOARDING_DISMISSED_KEY` localStorage logic lives on the Coder Agent tab; it has outlived its purpose.
- Tab count is 9 and growing; the horizontal bar no longer scales and gives no scope cue.

This is a **Web-only** shell/IA refactor. No Server / runner / CLI, HTTP API, domain, or persistence change is involved.

## Goals / Non-Goals

**Goals:**

- Split settings routing by scope: app sections at `/settings/*` (outside `ProjectRouteScope`, unguarded by the project-existence gate); project sections stay at `/:projectName/settings/*`.
- Replace the horizontal tab bar with a left sub-navigation grouped into **Application** / **Project**, with keyboard support (arrow keys, roving tabindex, `aria-current="page"`).
- Keep global settings reachable when the system has no project.
- Preserve existing deep links: legacy `/:projectName/settings/<global-section>` URLs redirect to `/settings/<global-section>`.
- Align each section heading with its nav label; move clarifying copy into the section description.
- Surface an overflow affordance on narrow views instead of silent clipping.
- Route Settings Search selections to the entry's correct scope.
- Remove `OnboardingBanner` and all its references.

**Non-Goals:**

- Redesigning the content inside any section (shell / navigation / IA only).
- Adding or removing settings sections.
- Changing Settings Search's matching, registry, or shortcut binding.

## Decisions

### D1 — One source of truth for section scope

A single module (e.g. a new `pages/settings/lib/sections.ts` or the existing `SECTION_META` extended with a `scope: 'application' | 'project'` field) classifies every section, and a helper `isApplicationSection(section)` / `sectionScope(section)` derives the answer. Every scope-dependent site reads this — `App.tsx` (redirect rules), the sub-nav (grouping + target URL), and Settings Search (navigation target).

- **Chosen:** a single exported constant + helper, imported by all consumers. Duplication across routing, nav, and search is the primary failure mode this change must avoid; one SOT makes a misclassification a compile-time-visible single edit.
- **Alternative rejected:** encoding the split separately in each consumer (route list, nav list, search). Cheaper to write but the three lists drift; a section added later would need to be updated in 3+ places.

### D2 — Route structure: app tree outside the project scope, project tree inside

In `App.tsx`:

- Add a `/settings/*` route tree **outside** `ProjectRouteScope` (sibling to the `ProjectGuard`-wrapped subtree) so it carries no project-name segment and never depends on a selected project. `/settings` index redirects to `/settings/ai`.
- Keep `/:projectName/settings/:section` inside `ProjectRouteScope` for **project** sections.
- Add a **legacy redirect**: a route element under `/:projectName/settings/:section` that, when `section` resolves to an application section, `<Navigate replace>` to `/settings/<section>`. Project sections fall through and render normally. `replace` keeps the legacy URL out of history.

- **Alternative considered:** keep all sections under `/:projectName/settings/*` and treat scope only as a display concern. Rejected — it violates the spec (URL must reflect scope) and keeps global settings unreachable with zero projects.
- **Alternative considered:** render app sections under both scopes. Rejected — two URLs for one resource breaks the "URL reflects scope" guarantee and complicates the active-state logic.

### D3 — Extend the ProjectGuard bypass to the whole `/settings/*` tree

Change the exact-match guard (`ProjectGuard.tsx:19`, `pathname === '/settings'`) to a prefix check covering the full tree (e.g. `pathname === '/settings' || pathname.startsWith('/settings/')`) so the project-existence "No projects yet" prompt never applies to global settings. Logs already uses the same bypass pattern and is unaffected.

- **Trade-off:** the bypass is now broader. The project section at `/:projectName/settings/<project-section>` remains inside `ProjectRouteScope`, so it is still gated; only the `/settings/*` tree is exempt. This matches the spec's "no-project reachability of global settings" requirement exactly.

### D4 — Left sub-navigation built from links, not Radix Tabs

Replace the `Tabs`/`TabsList`/`TabsTrigger` block with a vertical `<nav>` containing two visually distinct groups (Application / Project), each a list of anchor links. The active link gets `aria-current="page"`.

- **Chosen: route-based links.** Navigation here changes the URL and is therefore link semantics, not in-place tab-panel semantics. Links additionally give right-click/middle-click "open in new tab" and native href behavior for free.
- **Alternative rejected:** Radix `Tabs` with `orientation="vertical"`. It provides arrow-key + roving tabindex out of the box, but emits `role="tab"` + `aria-selected`, which is wrong for route-based page navigation and conflicts with the spec's `aria-current="page"` requirement. We'd be fighting the primitive.

Keyboard support (arrow keys + roving tabindex) is layered onto the link list via a small, dedicated hook (e.g. `useRovingTabindex`) that: collects the item refs, keeps `tabIndex` at `0` on the active item and `-1` elsewhere, and moves focus to the adjacent item on ArrowUp/ArrowDown (and ArrowLeft/ArrowRight for good measure). The hook lives next to the sub-nav and is unit-tested in isolation.

### D5 — Scope-aware navigation helper (replaces unconditional `useProjectPath`)

Introduce `useSettingsSectionPath()` (or equivalent) that returns the correct URL for a section based on D1's scope classification: `/settings/<section>` for application sections, `/:projectName/settings/<section>` for project sections. Both the sub-nav and Settings Search use it instead of `useProjectPath(...)`.

- Project sections need a current project; if none is selected the helper still produces the project-scoped path (the section content already renders a "No project selected" state, unchanged).
- **Trade-off:** two path helpers now coexist (`useProjectPath` for general app nav, `useSettingsSectionPath` for settings). Acceptable — the settings scope rule is specific enough to deserve its own helper rather than overloading the general one with a scope flag.

### D6 — Overflow affordance via scroll measurement

The sub-nav scroll container is measured (`scrollHeight > clientHeight` plus `scrollTop` proximity) to toggle a visible cue — a bottom (and top, when scrolled) gradient fade — only when content overflows. The check runs on mount, on resize, and on scroll.

- **Chosen:** runtime measurement toggling a CSS gradient. Matches the spec's "when it overflows" precisely and is cheap.
- **Alternative rejected:** an always-on CSS mask. Simpler but surfaces a cue even when nothing overflows, which is noise and fails the spec's conditional requirement.
- **Testing note:** the overflow state must be asserted via mocked `scrollHeight`/`clientHeight` (the same pattern already used in `MarkdownReader.test.tsx` and `EpicListPage.test.tsx`) — no real layout / timing.

### D7 — Sidebar Settings entry always visible, links to app scope

`AppSidebar.tsx`: drop the `currentProject !== null` filter for the settings item, and point it at `/settings/ai` directly (**not** wrapped in `toProjectPath`, since it is now app-scoped). Active-state detection is broadened so the entry stays active on any settings route (`/settings/*` or `/:projectName/settings/*`), consistent with the existing `segments.includes('settings')` approach already in `Header.tsx:87`.

### D8 — Onboarding removal is a pure deletion

Delete `OnboardingBanner.tsx` and its test; remove `showOnboarding` state, `ONBOARDING_DISMISSED_KEY`, the `dismissOnboarding` handler, and the `s.key === 'ai' && showOnboarding` render branch from `SettingsPage.tsx`. Settings-page tests that assert on the banner are removed. No replacement surface is added.

### D9 — Section heading = nav label

`SECTION_META`'s `label` becomes the single source for both the nav item and the section's visible heading. Each section component renders an `<h2>` (or equivalent) equal to its label, with clarifying copy moved into the section description. Driven by the same metadata map to keep them provably equal.

## Risks / Trade-offs

- **[Deep-link redirect misclassification]** A section is added to the wrong scope list → legacy URLs redirect incorrectly or project URLs stop resolving. -> **Mitigation:** D1 single source of truth; spec scenario "Project-section project URLs are not redirected" is covered by an explicit test asserting `repositories` does not redirect.
- **[Project section reached at app scope]** Navigating to `/settings/repositories` is ambiguous (no project). -> **Mitigation:** the `/settings/:section` route redirects project sections to the current project's `/:projectName/settings/<section>`; with no project, the existing "No project selected" content renders. Covered by a test.
- **[Broken bookmarks / in-app jumps]** Sidebar, Settings Search, and external bookmarks rely on the old project-scoped global URLs. -> **Mitigation:** D2 legacy `<Navigate replace>` redirect preserves them; Settings Search updated via D5.
- **[Keyboard a11y regression]** Hand-rolled roving tabindex could regress vs. Radix's battle-tested handling. -> **Mitigation:** dedicated unit tests for arrow traversal, focus wrap, and `aria-current="page"` on the active item; hook is isolated and small.
- **[Sidebar active-state flicker]** Crossing the app/project boundary changes the URL prefix while staying in Settings. -> **Mitigation:** D7 active detection keys off the `settings` segment, not a fixed prefix.
- **[Overflow cue depends on layout measurements]** jsdom has no layout. -> **Mitigation:** D6 mocks `scrollHeight`/`clientHeight` per the established pattern; no `while(now < deadline)` or timing assertions.

## Migration Plan

This is a pure frontend refactor with no data or API migration. Deploy steps:

1. Land the scope SOT (D1), route split + legacy redirect (D2), and ProjectGuard bypass (D3) together — the redirect makes the change bookmark-safe from the first deploy.
2. Swap the horizontal tab bar for the left sub-nav (D4–D6) and update Settings Search (D5).
3. Update the sidebar entry (D7) and align headings (D9).
4. Delete `OnboardingBanner` and its assertions (D8).
5. Update affected tests: `SettingsPage.test.tsx` (new routing/scope, drop onboarding assertions), `SettingsSearch.test.tsx` (scope-correct navigation), `Header.test.tsx` (already covers `/settings/ai` and project-scoped settings), `AppSidebar.test.tsx` (settings always visible).
6. Verify: `npm run typecheck -w packages/web` and `npm run test:run -w packages/web` are green.

**Rollback:** revert the commit(s); the legacy redirect makes a rollback safe because both old and new URLs resolve throughout — no stale client state or persisted migration is involved.

## Open Questions

- Should the `useRovingTabindex` hook (D4) be promoted to `shared/ui` for reuse by future nav surfaces, or stay co-located with the settings sub-nav until a second consumer appears? Lean: co-locate now, promote on second use.
- For the overflow affordance (D6), is a gradient fade sufficient, or do we also want an explicit chevron/"more" affordance on touch devices where hover-scroll cues are absent? The spec permits any of gradient/arrow/"more"; a gradient is the minimum that satisfies it.
