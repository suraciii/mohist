# Review Report

## Result: FAIL

## Findings

1. High: Epic list progress is always wrong.
File: `packages/cli/src/services/epic-service.ts:41-44,104-109,122-131`
`list()` calls `withProgress()`, but `withProgress()` always passes `[]` into `computeProgress()`. That makes every listed Epic report `0/0`, no `nextIssue`, and `readyToMarkDone: false` even when linked issues exist. This breaks the API, CLI, and Web list requirements that depend on backend-projected progress.
Suggested fix: In `withProgress()`, load linked issues with `this.epicRepo.getLinkedIssues(epic.id)` and pass them into `computeProgress(...)`.

2. High: Epic membership accepts non-existent issue ids and can persist dangling links.
File: `packages/cli/src/services/epic-service.ts:52-64`
File: `packages/cli/src/db/migrations.ts:1270-1276`
`addIssue()` verifies the Epic exists, but never verifies that the target issue exists. The `epic_issues` table also has no foreign key on `issue_id`, so the system can store memberships to missing issues. `getLinkedIssues()` joins against `issues`, which means these broken links disappear from detail/progress reads while still occupying the unique membership slot. This violates the spec requirement to link existing issues only and risks corrupted membership state.
Suggested fix: Validate issue existence before insert in the service layer, and add `REFERENCES issues(id) ON DELETE ...` for `issue_id` in the migration if the schema allows it.

3. High: The required Epic detail workflow is missing from the Web UI.
File: `packages/cli/web/src/App.tsx:104-118`
File: `packages/cli/web/src/components/EpicListPage.tsx:45-48`
File: `packages/cli/web/src/components/IssueDetailPage.tsx:358-366`
The app only registers `/epics`; there is no `/epic/:id` route. The only Epic components present are `EpicListPage.tsx` and `EpicCreateDialog.tsx`. Both the Epic list cards and the Issue Detail backlink navigate to `/epic/${id}`, which currently has no matching route or detail page. That means users cannot open Epic detail, add/remove linked issues, or run done/close lifecycle actions from the Web UI.
Suggested fix: Add an `EpicDetailPage` plus a `/epic/:id` route, then wire it to `useEpic`, `useAddEpicIssue`, `useRemoveEpicIssue`, `useMarkEpicDone`, and `useCloseEpic`.

## Spec Compliance

### cli-interface/spec.md

- PASS: `mo epic create` exists and calls `POST /epics`.
Evidence: `packages/cli/src/cli/commands/epic.ts:58-91`
- FAIL: `mo epic list` must print correct progress and next issue information, but backend list projection is always zeroed.
Evidence: `packages/cli/src/cli/commands/epic.ts:93-146`, `packages/cli/src/services/epic-service.ts:41-44,104-109,122-131`
- PASS: `mo epic show <id>` prints description, status, priority, progress, next issue, and linked issues.
Evidence: `packages/cli/src/cli/commands/epic.ts:148-195`
- PASS: `mo epic add-issue` and `mo epic remove-issue` exist and duplicate-membership errors are readable.
Evidence: `packages/cli/src/cli/commands/epic.ts:197-249`
- PASS: `mo epic done <id>` and `mo epic close <id>` update Epic lifecycle via API.
Evidence: `packages/cli/src/cli/commands/epic.ts:251-295`
- PASS: No Epic start command is exposed.
Evidence: `packages/cli/src/cli/commands/epic.ts:51-295`

### epic-tracking/spec.md

- PASS: Epic model is separate and uses `active/done/closed`.
Evidence: `packages/cli/src/db/migrations.ts:1242-1292`, `packages/cli/src/types/index.ts` Epic types, `packages/cli/src/db/epic-repo.ts:56-75`
- FAIL: Adding an issue should link an existing issue; current code can link missing issue ids.
Evidence: `packages/cli/src/services/epic-service.ts:52-64`, `packages/cli/src/db/migrations.ts:1270-1276`
- PASS: Removing a link only removes membership.
Evidence: `packages/cli/src/services/epic-service.ts:66-76`, `packages/cli/src/db/epic-repo.ts:124-130`
- PASS: Duplicate primary membership is rejected with Epic identity.
Evidence: `packages/cli/src/services/epic-service.ts:58-63`, `packages/cli/src/api/epics.ts:121-134`
- FAIL: Listed Epic progress must reflect linked issues; list projection is incorrect.
Evidence: `packages/cli/src/services/epic-service.ts:41-44,104-109`
- PASS: Detail progress computes delivered/total/blocked/active/next issue.
Evidence: `packages/cli/src/services/epic-service.ts:112-174`
- PASS: Lifecycle actions only change Epic status and do not auto-complete.
Evidence: `packages/cli/src/services/epic-service.ts:86-102`

### http-api/spec.md

- PASS: `POST /api/epics`, `GET /api/epics`, `GET /api/epics/:id`, membership routes, `done`, and `close` routes exist.
Evidence: `packages/cli/src/api/epics.ts:12-237`
- FAIL: `GET /api/epics` must include progress and next issue data for each Epic, but the service returns zeroed list progress.
Evidence: `packages/cli/src/api/epics.ts:12-27`, `packages/cli/src/services/epic-service.ts:41-44,104-109`
- PASS: Duplicate membership returns a structured error with existing Epic details.
Evidence: `packages/cli/src/api/epics.ts:121-134`
- PASS: Issue detail includes primary Epic summary and unlinked issues return null.
Evidence: `packages/cli/src/api/issues.ts:1311-1338`
- PASS: Board lanes remain issue-only by construction because Epics live in separate tables/routes.
Evidence: `packages/cli/src/db/migrations.ts:1242-1292`, `packages/cli/src/server/index.ts:300-301`

### local-issue-store/spec.md

- PASS: Epic records persist title, description, priority, status, and timestamps.
Evidence: `packages/cli/src/db/migrations.ts:1242-1251`, `packages/cli/src/db/epic-repo.ts:56-75`
- PASS: Membership persists `epic_id`, `issue_id`, and `created_at`, with unique `issue_id`.
Evidence: `packages/cli/src/db/migrations.ts:1270-1281`
- PASS: Membership/lifecycle actions do not update issue workflow rows.
Evidence: `packages/cli/src/db/epic-repo.ts:116-130`, `packages/cli/src/services/epic-service.ts:66-102`
- PASS: Storage supports list/detail/backlink queries.
Evidence: `packages/cli/src/db/epic-repo.ts:77-187`
- FAIL: Persistence layer does not enforce that membership references a real issue row.
Evidence: `packages/cli/src/db/migrations.ts:1270-1276`

### web-ui/spec.md

- PASS: Navigation includes `Epics` and Board lanes remain issue-only.
Evidence: `packages/cli/web/src/components/Header.tsx:153-162`, `packages/cli/web/src/components/MobileBottomNav.tsx:32-33`, separate route in `packages/cli/web/src/App.tsx:117`
- PASS: Create Epic form asks only for title, description, and priority.
Evidence: `packages/cli/web/src/components/EpicCreateDialog.tsx:49-123`
- FAIL: Epic list should show backend-provided progress/next issue, but list backend projection is incorrect.
Evidence: `packages/cli/web/src/components/EpicListPage.tsx:60-90`, `packages/cli/src/services/epic-service.ts:104-109`
- PASS: List distinguishes active/done/closed groups.
Evidence: `packages/cli/web/src/components/EpicListPage.tsx:101-167`
- FAIL: Epic detail page is missing.
Evidence: `packages/cli/web/src/App.tsx:104-118`; no `EpicDetailPage` component is present under `packages/cli/web/src/components/`
- FAIL: Add linked issue UI is missing.
Evidence: No Epic detail route/page; only `EpicListPage.tsx` and `EpicCreateDialog.tsx` exist for Epic UI.
- FAIL: Remove linked issue UI is missing.
Evidence: No Epic detail route/page; only `EpicListPage.tsx` and `EpicCreateDialog.tsx` exist for Epic UI.
- FAIL: Lifecycle actions from Epic detail are missing.
Evidence: No Epic detail route/page; `useMarkEpicDone` and `useCloseEpic` are defined in `packages/cli/web/src/hooks/useQueries.ts:652-678` but not wired to any page.
- FAIL: Issue detail backlink cannot open the Epic detail page because the route does not exist.
Evidence: `packages/cli/web/src/components/IssueDetailPage.tsx:358-366`, `packages/cli/web/src/App.tsx:104-118`
- PASS: Unlinked issue hides backlink because rendering is conditional.
Evidence: `packages/cli/web/src/components/IssueDetailPage.tsx:358-367`

## Tests

- PASS: Targeted backend regression tests passed.
Command: `npm test -- epic-regression.test.ts`
- PASS: Build passed.
Command: `npm run build`
- FAIL: Automated coverage does not catch the broken list projection or the missing Epic detail route/page.
Evidence: `packages/cli/tests/epic-regression.test.ts` has no assertion over `epicService.list()` with linked issues and no Web tests for `/epic/:id` navigation.

## Overall

Overall result is FAIL because there are error-level spec and correctness issues in list projection, membership validation, and the Web Epic detail surface.

<promise>FAIL</promise>
