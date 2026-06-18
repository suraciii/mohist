## Why

Mohist's default landing page is the Kanban board, but a user returning to the app first wants "overall status + achievement," not an issue-card pile. Epic #9 introduces a Dashboard as the new default home, and this issue is the composition host: it must provide the Dashboard route as an empty skeleton with zone mount points (Attention/Pulse/Productivity/Digest) that issues E/F/G/H will later fill, while preserving Kanban fully under a demoted "Issues" entry. Without this host layer, the downstream zone issues have nowhere to mount and the default-landing change cannot land independently.

## What Changes

- Add a new Dashboard page as the default landing surface (`/`), rendered as an empty skeleton: page container plus four zone mount-point placeholders (Attention, Pulse, Productivity, Digest), with no zone content implemented.
- **BREAKING** (UX): Kanban ceases to be the default home. It moves to a dedicated `Issues` route reachable from primary navigation; root path lands on Dashboard instead of Kanban.
- Relocate the project empty-state ("No projects yet" → Create Project) from the current HomePage to the Dashboard page so the empty/onboarding path still works at the new default landing.
- Restructure primary navigation in both `AppSidebar` (desktop) and `MobileBottomNav` (mobile) to: Dashboard / Issues / Activity / Epics / Logs / Settings / Archived — kept in sync across desktop and mobile.
- Preserve all existing Kanban behavior on its new route: filtering, search, sort, URL query state (`?priorities=...&labels=...`), and existing tests must not regress.
- Leave all other page routes unchanged.

## Capabilities

### New Capabilities

- `dashboard-shell`: The Dashboard page as a composition host — default landing route, page container, and the four zone mount-point slots (Attention/Pulse/Productivity/Digest) that downstream issues E/F/G/H will fill. Defines the slot contract and empty-state behavior without implementing any zone content.

### Modified Capabilities

- `web-ui`: Primary navigation restructured to lead with Dashboard and demote Kanban to an `Issues` entry, synchronized across `AppSidebar` and `MobileBottomNav`; the project empty-state ("No projects yet" → Create Project) moves from the old HomePage to the new Dashboard landing; Kanban behavior is preserved unchanged on its relocated route.

## Impact

- **Routing**: `packages/web/src/app/App.tsx` — root/index route resolves to the Dashboard skeleton instead of HomePage; Kanban becomes the `Issues` route. Root redirect (`NavigateToCurrentProject`) target unchanged in shape.
- **Pages**: `packages/web/src/pages/home/ui/HomePage.tsx` currently renders `KanbanBoard` directly and owns the project empty-state; the empty-state moves to the new Dashboard page and HomePage becomes the Issues/Kanban surface.
- **New page**: a Dashboard page component (under `packages/web/src/pages/`) providing the container + four zone slot placeholders.
- **Navigation widgets**: `packages/web/src/widgets/app-shell/ui/AppSidebar.tsx` and `MobileBottomNav.tsx` gain Dashboard and Issues entries (desktop + mobile synchronized) with the canonical nav order Dashboard / Issues / Activity / Epics / Logs / Settings / Archived.
- **Dependencies**: `useProjects` / `CreateProjectDialog` reused for the relocated empty-state; no new API or persistence changes — this is a web-layer-only restructure.
- **Tests**: existing Kanban tests must pass unchanged on the relocated route; new coverage for Dashboard-as-default-landing, nav-item presence/order on desktop and mobile, and the relocated project empty-state.
- **Downstream contract**: the four zone slots established here are the mount points issues E/F/G/H will target; their contracts are intentionally unspecified in this proposal and belong in the `dashboard-shell` spec.
