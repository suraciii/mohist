# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

_None._

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: info
  Scope: test command selection
  Evidence: The repo-root `npm test` script in `package.json` runs `dotnet test Mohist.sln`, so Vitest file filters such as `--run src/...` are not valid from the root. Frontend-targeted verification for this change was run from `packages/web` using the package's `test:run` script.
  SuggestedAction: Continue using `npm run test:run -- <src/...test.tsx>` from `packages/web` for focused frontend checks, or add a future root-level frontend test wrapper if desired.
  Status: out-of-scope

## Acceptance Criteria Evidence

- AC1: `packages/web/src/app/App.tsx:53` nests project routes under `/:projectName`, and `packages/web/src/app/App.tsx:54` renders `DashboardPage` on the index route. `packages/web/src/pages/dashboard/ui/DashboardPage.test.tsx:88` verifies the dashboard does not render Kanban controls.
- AC2: `packages/web/src/widgets/app-shell/ui/AppSidebar.tsx:41` defines `Dashboard` then `Issues`; `packages/web/src/widgets/app-shell/ui/MobileBottomNav.tsx:21` defines the same leading mobile destinations. Order/presence tests are in `packages/web/src/widgets/app-shell/ui/AppSidebar.test.tsx:68`, `packages/web/src/widgets/app-shell/ui/AppSidebar.test.tsx:95`, and `packages/web/src/widgets/app-shell/ui/MobileBottomNav.test.tsx:49`.
- AC3: `packages/web/src/app/App.tsx:55` routes `/:projectName/issues` to `IssuesPage`, which renders `KanbanBoard` in `packages/web/src/pages/issues/ui/IssuesPage.tsx:20`. `packages/web/src/pages/issues/ui/IssuesPage.routing.test.tsx:83` verifies `/issues` renders the board, and the unchanged URL-backed Kanban behavior is covered by `packages/web/src/widgets/kanban-board/ui/kanban-board-query.test.tsx` in the verification command.
- AC4: `packages/web/src/pages/dashboard/ui/DashboardPage.tsx:29` renders the `No projects yet` empty-state and `Create Project` action when no projects exist; `packages/web/src/pages/dashboard/ui/DashboardPage.test.tsx:100` and `packages/web/src/pages/dashboard/ui/DashboardPage.test.tsx:115` cover the empty-state and dialog opening.
- AC5: The focused verification included `src/widgets/kanban-board/ui/kanban-board-query.test.tsx`, and the full command passed 6 test files / 84 tests.
- Header regression repair: `packages/web/src/widgets/app-shell/ui/Header.tsx:34` returns `Issue #N` for project-scoped issue detail paths, and `packages/web/src/widgets/app-shell/ui/Header.test.tsx:72` covers `/:projectName/issues/:number` with route params.

## Verification

- Issue read: `mo issue show 163 --project-id proj_f6c141d63b6243bfbb481737b2243b87`.
- Candidate scope inspected: `git diff master...HEAD --name-only` and changed source/tests under `packages/web/src/app`, `packages/web/src/pages/dashboard`, `packages/web/src/pages/issues`, and `packages/web/src/widgets/app-shell`.
- Tests passed: `npm run test:run -- src/pages/dashboard/ui/DashboardPage.test.tsx src/pages/issues/ui/IssuesPage.routing.test.tsx src/widgets/app-shell/ui/AppSidebar.test.tsx src/widgets/app-shell/ui/MobileBottomNav.test.tsx src/widgets/app-shell/ui/Header.test.tsx src/widgets/kanban-board/ui/kanban-board-query.test.tsx` from `packages/web` passed 6 files / 84 tests.

<promise>PASS</promise>
