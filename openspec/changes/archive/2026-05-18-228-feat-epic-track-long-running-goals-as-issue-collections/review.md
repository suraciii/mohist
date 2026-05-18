# Review Report

## Result

PASS with warnings.

## Findings

No error-level correctness, security, or spec-compliance defects were found in the implemented Epic feature set.

## Warnings

1. `packages/cli/src/cli/commands/epic.ts:69`
Suggested change: split `setupEpicCommands` into smaller command-specific helpers. The current function is over 200 lines and exceeds the requested complexity target, which will make future CLI changes harder to review safely.

2. `packages/cli/web/src/components/EpicDetailPage.tsx:103`
Suggested change: extract the header/actions block and linked-issues management block into smaller components or hooks. The page component is large enough that future changes to lifecycle actions and membership UI will be harder to reason about.

## Review Dimensions

- Correctness: PASS. Epic creation, progress projection, lifecycle actions, membership uniqueness, issue backlink data, CLI wiring, and workflow isolation are all covered in code and regression tests.
- Complexity: PASS with warnings. A few new functions/components exceed the requested size target, but this does not currently produce a behavioral defect.
- Test Coverage: PASS. Epic backend and CLI regression tests passed via `npm test -- epic-regression.test.ts epic-cli.test.ts`. Full package build and typecheck passed via `npm run build`.
- Security: PASS. API routes validate required inputs, use parameterized DB access through repos, and return structured errors without exposing secrets.
- Spec Compliance: PASS. All acceptance criteria below have concrete implementation evidence.

## Acceptance Criteria

1. PASS: User can create Epic with `title`, `description`, and `priority`.
Evidence: `packages/cli/src/api/epics.ts:43-89` validates and creates the Epic; `packages/cli/src/services/epic-service.ts:67-78` initializes it; `packages/cli/web/src/components/EpicCreateDialog.tsx:49-123` implements the web form.

2. PASS: User can view Epic list showing `status`, `progress`, and `next issue`.
Evidence: `packages/cli/src/api/epics.ts:21-41` returns list data; `packages/cli/src/services/epic-service.ts:80-83,180-229` projects progress; `packages/cli/web/src/components/EpicListPage.tsx:97-174` renders grouped list cards with delivered/total and next issue.

3. PASS: User can open Epic detail and see `description`, `progress`, `next issue`, and `linked issues`.
Evidence: `packages/cli/src/services/epic-service.ts:85-89,170-177` returns detail with linked issues and progress; `packages/cli/src/api/epics.ts:99-120` exposes it; `packages/cli/web/src/components/EpicDetailPage.tsx:168-286` renders all required detail sections.

4. PASS: User can add an existing issue to an Epic.
Evidence: `packages/cli/src/services/epic-service.ts:91-122` performs membership add with validation; `packages/cli/src/api/epics.ts:130-205` exposes `POST /api/epics/:id/issues`; `packages/cli/web/src/components/EpicDetailPage.tsx:236-262` provides the add UI.

5. PASS: User can remove an issue from an Epic.
Evidence: `packages/cli/src/services/epic-service.ts:124-134` removes membership only; `packages/cli/src/api/epics.ts:207-244` exposes `DELETE /api/epics/:id/issues/:issueId`; `packages/cli/web/src/components/EpicDetailPage.tsx:264-285` provides remove controls.

6. PASS: One issue belongs to at most one primary Epic, with clear duplicate-add feedback.
Evidence: `packages/cli/src/db/migrations.ts:1277-1285` enforces `UNIQUE (issue_id)` in `epic_issues`; `packages/cli/src/services/epic-service.ts:106-120` translates duplicates into `DuplicateEpicMembershipError`; `packages/cli/src/api/epics.ts:157-169` returns structured duplicate-membership errors; `packages/cli/web/src/components/EpicDetailPage.tsx:87-101,258-262` displays clear duplicate feedback.

7. PASS: Issue detail page shows linked Epic backlink.
Evidence: `packages/cli/src/api/issues.ts:1352-1379` includes `primaryEpic` in issue detail; `packages/cli/web/src/components/IssueDetailPage.tsx:358-367` renders `Part of Epic` and navigates to Epic detail.

8. PASS: Epic progress is projected from linked issues as delivered / total.
Evidence: `packages/cli/src/services/epic-service.ts:180-229` computes read-time progress; `packages/cli/tests/epic-regression.test.ts:110-131` verifies delivered and total counts.

9. PASS: Epic next issue follows blocked, then active, then backlog, else ready-to-mark-done.
Evidence: `packages/cli/src/services/epic-service.ts:202-229` implements the priority order; `packages/cli/tests/epic-regression.test.ts:271-372` covers blocked, active, backlog, ready-to-mark-done, interrupted, and paused cases.

10. PASS: User can mark Epic `done` or `closed`, with no automatic completion.
Evidence: `packages/cli/src/services/epic-service.ts:144-159` implements explicit lifecycle actions only; `packages/cli/src/api/epics.ts:246-305` exposes `/done` and `/close`; `packages/cli/web/src/components/EpicDetailPage.tsx:179-199` renders both actions; `packages/cli/tests/epic-regression.test.ts:375-430` verifies linked issues are unchanged.

11. PASS: CLI supports `mo epic create/list/show/add-issue/remove-issue/close` basic operations, and also `done`.
Evidence: `packages/cli/src/cli/commands/epic.ts:76-307` implements `create`, `list`, `show`, `add-issue`, `remove-issue`, `done`, and `close`; `packages/cli/tests/epic-cli.test.ts:15-98` verifies list output includes status, progress, and next state.

12. PASS: Epic does not appear in issue workflow Board lanes and cannot be started.
Evidence: `packages/cli/src/db/migrations.ts:1248-1285` stores Epics in separate `epics` and `epic_issues` tables; `packages/cli/src/server/index.ts:299-301` wires Epic routes separately from issue routes; `packages/cli/tests/epic-regression.test.ts:472-500` verifies Epics are not issue rows and have no workflow fields or start path.

## Verification Notes

- Automated tests passed: `npm test -- epic-regression.test.ts epic-cli.test.ts`
- Build and typecheck passed: `npm run build`

## Residual Risk

- Web UI Epic behavior is covered mainly by component-level tests plus successful production build, not end-to-end browser automation. That is acceptable for this change, but future workflow-heavy Epic interactions would benefit from a small end-to-end smoke test.

<promise>PASS</promise>
