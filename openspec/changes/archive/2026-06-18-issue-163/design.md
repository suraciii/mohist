## Context

Epic #9 makes a Dashboard the default home of Mohist. This issue is the **composition host**: it ships an empty Dashboard skeleton as the default landing, demotes the Kanban from default home to a primary-nav entry `Issues`, and moves the project empty-state ("No projects yet" → Create Project) onto the Dashboard. The four zone views (Attention/Pulse/Productivity/Digest) are explicitly out of scope — they are mounted by downstream issues E/F/G/H into the slots this issue establishes.

**Current state (web layer only, no API/persistence involved):**

- Routing lives in `packages/web/src/app/App.tsx`. The project index route renders `HomePage` (`App.tsx:53`), and `ProjectNameGuard` also falls back to `HomePage` when there are no projects (`App.tsx:101`). Both are Kanban-as-home today.
- `HomePage` (`packages/web/src/pages/home/ui/HomePage.tsx`) currently does two jobs: it renders `KanbanBoard`, and it owns the project empty-state. These two concerns must split.
- `Header.usePageTitle()` maps the project root to the title `Board` (`Header.tsx:13`). `Header.test.tsx:44` and `Header.test.tsx:80-85` assert this.
- `AppSidebar` (`AppSidebar.tsx`) and `MobileBottomNav` (`MobileBottomNav.tsx`) define primary navigation; the desktop sidebar leads with a `Board`/home item that targets the project root.
- The mobile `FAB` (create-issue shortcut) is shown via `isProjectRoot()` (`App.tsx:68`, `App.tsx:111-113`), i.e. only on the project root.
- Kanban behavior (filter/search/sort/URL query) is fully covered by `kanban-board-query.test.tsx`, which renders `KanbanBoard` in isolation via `MemoryRouter` and is **route-agnostic** — so relocating the Kanban to a new route does not regress these tests.

**Constraints:** Web App-Shell bounded context only — no domain contexts, no API or persistence migration. Kanban functionality must be preserved unchanged (AC #3, #5). Other page routes must not change.

## Goals / Non-Goals

**Goals:**
- Project root (`/` and `/:projectName`) lands on an empty Dashboard skeleton with four named zone mount-point placeholders (Attention/Pulse/Productivity/Digest) and no zone content.
- Kanban moves to a dedicated `Issues` route reachable from primary nav, with all existing filter/search/sort/URL-query behavior preserved.
- Primary navigation (desktop `AppSidebar` + mobile `MobileBottomNav`) is synchronized and leads with `Dashboard` then `Issues`, in the canonical order Dashboard / Issues / Activity / Epics / Logs / Settings / Archived.
- The project empty-state moves from `HomePage` to the Dashboard and works there.
- Establish a stable, minimal slot contract that downstream issues E/F/G/H can target.

**Non-Goals:**
- Implementing any zone content (Attention/Pulse/Productivity/Digest) — owned by E/F/G/H.
- Changing Kanban functionality or its tests.
- Adjusting any other page route (issue detail, activity, epics, logs, settings, archived).
- Backend, API, or persistence changes.

## Decisions

### Decision 1: Kanban lives at `/issues` (the issues index), reusing the existing `issues` path segment

The Kanban becomes the index surface of the `issues` path: `/:projectName/issues` → Kanban board, while the existing `/:projectName/issues/:number` → Issue Detail stays unchanged. React Router matches the more specific `issues/:number` for `/issues/123` and the `issues` index for `/issues`, so the two coexist without conflict.

- **Alternative considered:** a separate `/board` route. Rejected because the nav label is `Issues` (per the issue text), and a `/board` path would mismatch the label and add route surface. Reusing `issues` keeps label, path, and the existing issue-detail route aligned.
- **Trade-off:** `/issues` being a full board while `/issues/:number` is a detail view is a mild semantic mix, but it matches well-known patterns (e.g. GitHub's issues list vs. issue detail) and keeps the Non-Goal of "no other route changes".

### Decision 2: Split `HomePage` — new `DashboardPage` owns the landing + empty-state; the Kanban page is repurposed/renamed to `IssuesPage`

- Create `packages/web/src/pages/dashboard/ui/DashboardPage.tsx`. It owns: (a) the four zone-slot skeleton, and (b) the relocated project empty-state ("No projects yet" → Create Project) using the existing `useProjects` + `CreateProjectDialog`.
- Strip the empty-state from `HomePage` and have it render only `KanbanBoard`. Rename `HomePage` → `IssuesPage` (`packages/web/src/pages/issues/ui/IssuesPage.tsx`) so the codebase matches the new mental model (Dashboard is home; Issues is the board). `useDocumentTitle('Mohist', …)` moves to `DashboardPage` since it is the new default landing.
- `App.tsx` wires the index route (`App.tsx:53`) and the `ProjectNameGuard` no-projects fallback (`App.tsx:101`) to `DashboardPage`, and adds `<Route path="issues" element={<IssuesPage />} />`.

- **Alternative considered:** keep the empty-state inside `HomePage` and point it at the Dashboard. Rejected because it duplicates the split responsibility the spec establishes (`dashboard-shell` owns empty-state behavior) and leaves a misleadingly named component routing to `/issues`.
- **Trade-off:** the rename touches imports in `App.tsx` and `ProjectNameGuard`. This is low-risk and improves long-term clarity; the rename is recommended but could be deferred (keep the `HomePage` symbol) if minimal diff is preferred.

### Decision 3: Zone slots are named placeholder components with stable identities — no slot registry/context yet

`DashboardPage` renders a fixed layout with four `<DashboardZonePlaceholder name="Attention" />` (and Pulse/Productivity/Digest) components, each exposing a stable identity via `data-zone="attention|pulse|productivity|digest"` and a `data-testid`. Downstream issues E/F/G/H mount their zone UI by replacing the corresponding placeholder in the Dashboard composition (or, later, via a slot map).

- **Alternative considered:** a slot registry / React context where zones self-register, decoupling the Dashboard from its zones. Rejected as over-engineering for an empty-skeleton issue; it adds indirection before any zone exists. Defer until a downstream issue demonstrates the need for dynamic registration.
- **Trade-off:** the static-placeholder approach means downstream zone issues will edit `DashboardPage`'s composition. That is acceptable and keeps the host change minimal, discoverable, and testable now.

### Decision 4: Primary navigation order and grouping

Desktop sidebar (`AppSidebar`) `primaryNav` `Workspace` group becomes: `Dashboard` (project root), `Issues` (`/issues`), `Activity`, `Epics`; `Logs`, `Settings` remain under `Configure`; `Archived` moves to render last (leaving the `Workspace` group) so the overall order matches the canonical Dashboard / Issues / Activity / Epics / Logs / Settings / Archived. The `Issues` item replaces the prior `Board`/home item. `MobileBottomNav` gains `Dashboard` and `Issues` entries so mobile and desktop expose the same destinations (current mobile nav leads with Activity/Epics/More — it must be brought into sync).

- The existing `isNavActive()` helper already handles the project-root case and prefix matching, so the Issues item will correctly be active on both `/issues` and `/issues/:number` (issue detail), which is the desired behavior.
- **Alternative considered:** leave mobile nav leading with Activity. Rejected — AC #2 requires desktop + mobile synchronization.

### Decision 5: `Header` title map — root → `Dashboard`, `/issues` → `Issues`

Update `usePageTitle()` (`Header.tsx:13`): project root returns `Dashboard` (was `Board`); add `section === '/issues'` → `Issues` before the existing `/issues/` detail check (which already yields `Issue #N`). This requires updating the two `Header.test.tsx` assertions that expect `Board` on the home route (`Header.test.tsx:44`, `Header.test.tsx:80-85`) to `Dashboard`, plus adding an `/issues` → `Issues` assertion.

### Decision 6: Mobile `FAB` shows on both Dashboard and Issues routes

Today `isProjectRoot()` (`App.tsx:111-113`) gates the `FAB` to the project root. After the split, the Kanban (now at `/issues`, two segments) would lose the mobile create-issue FAB — a subtle regression. Generalize the gate to a helper that returns true for the Dashboard (project root) **and** the Issues route, preserving the Kanban's existing affordance while also giving the Dashboard skeleton a create-issue entry.

- **Alternative considered:** keep the FAB only on the project root (Dashboard). Rejected because it silently removes the create-issue shortcut from the Kanban on mobile.

## Risks / Trade-offs

- **[BREAKING UX] Root deep-links/bookmarks that expected the Kanban now land on the Dashboard.** -> This is intentional and called out in the proposal. Mitigation: `Issues` is one click away in both desktop and mobile nav; no silent redirect is added (a redirect would defeat the purpose of the new default landing).
- **[`/issues` index vs `/issues/:number` shadowing]** A misconfigured route could make the Kanban index swallow issue-detail paths. -> Mitigation: rely on React Router's specificity (more specific wins) and add a routing test asserting `/issues/123` renders Issue Detail while `/issues` renders the board.
- **[Existing `Header.test.tsx` assertions break]** The `Board`-on-home assertions fail once the title changes. -> Mitigation: update those assertions in the same change and add the new `Issues` title case.
- **[Stale `HomePage` naming]** If the rename to `IssuesPage` is deferred, a component named `HomePage` routed at `/issues` confuses future readers. -> Mitigation: perform the rename as part of this change (recommended); if deferred, leave a clear comment.
- **[Zone slot contract churn]** Static placeholders may need rework if downstream zones need dynamic registration. -> Mitigation: keep the slot surface minimal (stable `data-zone` identities only) so a future registry can be layered on without breaking the placeholders.

## Migration Plan

This is a web-layer-only change with no backend, API, or persistence migration.

1. Add `DashboardPage` (zone-slot skeleton + relocated empty-state) and the `DashboardZonePlaceholder` component.
2. Repurpose `HomePage` → `IssuesPage` (Kanban only, empty-state removed); move `useDocumentTitle` to `DashboardPage`.
3. Update `App.tsx` routing: index route and `ProjectNameGuard` no-projects fallback → `DashboardPage`; add `issues` index route → `IssuesPage`; generalize the FAB gate to Dashboard + Issues.
4. Update `AppSidebar` and `MobileBottomNav` nav items (Dashboard/Issues order, desktop + mobile sync).
5. Update `Header.usePageTitle()` (root → Dashboard, `/issues` → Issues) and fix `Header.test.tsx`.
6. Add tests: DashboardPage (4 slots, empty-state, no Kanban), nav presence/order (desktop + mobile), `/issues` routing (board vs detail).

**Rollback:** Revert the single change set. No data migration to undo; routes revert to Kanban-as-home.

## Open Questions

- **Zone slot composition mechanism:** static placeholders (this design) vs a registry/context. Defer the registry until E/F/G/H demonstrate dynamic-registration needs.
- **Dashboard layout/visual design:** the issue specifies only "page container + zone slots." The concrete visual arrangement of the four slots (grid order, responsive behavior) is not constrained by the spec; this implementation will choose a simple, neutral responsive layout and leave visual polish to Epic #9 follow-ups.
- **Whether to preserve a `Board` alias:** no legacy redirect to `/issues` is added. If analytics or external links later show breakage, a redirect can be introduced as a separate follow-up.
