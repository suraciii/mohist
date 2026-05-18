# Findings

1. Error: `paused` issues are projected as active/backlog work, which violates the spec's next-issue rule and can send users to the wrong next step. In `packages/cli/src/services/epic-service.ts:53-59`, `isActiveIssue` and `isBacklogIssue` both treat `IssueStatus.Paused` as eligible for `activeIssues`/`nextIssue`. The regression test at `packages/cli/tests/epic-regression.test.ts:358-371` locks this behavior in by expecting a paused issue to become `nextIssue`. The spec only allows `blocked`, then `active`, then `backlog`, otherwise ready-to-mark-done. A paused issue is a distinct state in this codebase, so this projection is broader than the accepted ordering.

# Fix Suggestions

- `packages/cli/src/services/epic-service.ts:53-59`
  Change `isActiveIssue` and `isBacklogIssue` to exclude `IssueStatus.Paused`, and decide whether paused work should be represented only in a separate count or simply excluded from `nextIssue`.
- `packages/cli/tests/epic-regression.test.ts:358-371`
  Replace the paused-work expectation with a spec-aligned assertion proving paused issues do not become `nextIssue` unless the product spec is updated.

# Quality Checks

- Typecheck: PASS via `npm run typecheck`
- Backend/CLI tests: PASS via `npm test -- tests/epic-regression.test.ts tests/epic-cli.test.ts`
- Web tests: PASS via `npm --prefix web run test:run -- src/components/EpicListPage.test.tsx src/components/EpicDetailPage.test.tsx src/components/IssueDetailPage.test.tsx src/components/Header.test.tsx`

# Spec Compliance

- PASS: User can create Epic with `title`, `description`, and `priority`.
  Evidence: API validation and create flow in `packages/cli/src/api/epics.ts:43-89`; web form in `packages/cli/web/src/components/EpicCreateDialog.tsx:19-125`; CLI command in `packages/cli/src/cli/commands/epic.ts:76-109`.
- PASS: User can view Epic list with status, progress, and next issue.
  Evidence: service list projection in `packages/cli/src/services/epic-service.ts:80-83,162-168`; API list route in `packages/cli/src/api/epics.ts:21-41`; web list UI in `packages/cli/web/src/components/EpicListPage.tsx:97-174`; CLI list output in `packages/cli/src/cli/commands/epic.ts:111-158`.
- PASS: User can open Epic detail and see description, progress, next issue, and linked issues.
  Evidence: detail projection in `packages/cli/src/services/epic-service.ts:85-89,170-177`; API detail route in `packages/cli/src/api/epics.ts:99-128`; web detail page in `packages/cli/web/src/components/EpicDetailPage.tsx:168-286`; CLI show output in `packages/cli/src/cli/commands/epic.ts:160-207`.
- PASS: User can add an existing issue to an Epic.
  Evidence: membership add in `packages/cli/src/services/epic-service.ts:91-122`; API route in `packages/cli/src/api/epics.ts:130-205`; web add UI in `packages/cli/web/src/components/EpicDetailPage.tsx:236-262`; CLI command in `packages/cli/src/cli/commands/epic.ts:209-237`.
- PASS: User can remove an issue from an Epic.
  Evidence: membership removal in `packages/cli/src/services/epic-service.ts:124-134`; API route in `packages/cli/src/api/epics.ts:207-244`; web remove UI in `packages/cli/web/src/components/EpicDetailPage.tsx:270-285`; CLI command in `packages/cli/src/cli/commands/epic.ts:239-261`.
- PASS: One issue can belong to at most one primary Epic and duplicate add returns clear error.
  Evidence: DB uniqueness in `packages/cli/src/db/migrations.ts:1277-1289`; duplicate handling in `packages/cli/src/services/epic-service.ts:14-23,106-121`; structured API error in `packages/cli/src/api/epics.ts:157-168`; readable web error in `packages/cli/web/src/components/EpicDetailPage.tsx:87-101,258-262`; readable CLI error in `packages/cli/src/cli/commands/epic.ts:224-231`.
- PASS: Issue detail page shows Epic backlink.
  Evidence: issue detail API payload in `packages/cli/src/api/issues.ts:1352-1379`; web backlink rendering in `packages/cli/web/src/components/IssueDetailPage.tsx:358-367`.
- PASS: Epic progress automatically projects delivered/total from linked issues.
  Evidence: projection logic in `packages/cli/src/services/epic-service.ts:180-229`; regression coverage in `packages/cli/tests/epic-regression.test.ts:110-151`.
- FAIL: Epic next issue uses blocked > active > backlog > ready-to-mark-done.
  Evidence: `packages/cli/src/services/epic-service.ts:53-59` classifies `paused` as active/backlog; `packages/cli/tests/epic-regression.test.ts:358-371` asserts a paused issue becomes `nextIssue`. This exceeds the accepted ordering and can change the recommended next issue.
- PASS: User can mark Epic `done` or `closed`, and done is not automatic.
  Evidence: lifecycle methods in `packages/cli/src/services/epic-service.ts:144-160`; API routes in `packages/cli/src/api/epics.ts:246-305`; web buttons in `packages/cli/web/src/components/EpicDetailPage.tsx:179-199`; CLI commands in `packages/cli/src/cli/commands/epic.ts:263-307`; no auto-complete behavior in `packages/cli/src/services/epic-service.ts:213-218`.
- PASS: CLI supports `mo epic create/list/show/add-issue/remove-issue/close` basic operations.
  Evidence: command group in `packages/cli/src/cli/commands/epic.ts:69-307`.
- PASS: Epic does not appear in issue workflow board lanes and cannot be started.
  Evidence: separate `epics` storage/type model in `packages/cli/src/types/index.ts:277-320` and `packages/cli/src/db/epic-repo.ts:25-47`; separate web routes in `packages/cli/web/src/App.tsx:105-120`; workflow-isolation tests in `packages/cli/tests/epic-regression.test.ts:472-500`; no `start` command in `packages/cli/src/cli/commands/epic.ts:69-307`.

# Overall

- Result: FAIL
- Reason: one error-level spec deviation in next-issue projection semantics.

<promise>FAIL</promise>
