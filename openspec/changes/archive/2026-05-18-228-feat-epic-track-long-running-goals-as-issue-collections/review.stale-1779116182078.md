# Review

## Result

FAIL

## Findings

1. High: `POST /api/epics` does not return a structured validation error for invalid create input, which violates `specs/http-api/spec.md`.
File: `packages/cli/src/api/epics.ts:33-55`
Evidence: invalid title/description/priority currently return only `{ success: false, error: string }` with HTTP 400. The required structured shape used elsewhere in this change includes `code` and `details`; see duplicate membership handling in `packages/cli/src/api/epics.ts:124-151` and the matching regression coverage in `packages/cli/tests/epic-regression.test.ts:571-619`.
Impact: clients cannot reliably distinguish field validation failures from generic bad requests, so the API does not meet the spec requirement "invalid input returns a structured validation error".
Suggested fix: return a stable validation payload such as `{ success: false, error, code: 'VALIDATION_ERROR', details: { field: 'title' } }` for each invalid field branch in `packages/cli/src/api/epics.ts:33-55`, and add API regression tests for invalid create payloads.

## Spec Compliance

1. PASS: User can create Epic with title, description, priority.
Evidence: create form asks only those fields in `packages/cli/web/src/components/EpicCreateDialog.tsx:50-98`; API create path persists active Epic in `packages/cli/src/api/epics.ts:29-64` and `packages/cli/src/services/epic-service.ts:56-67`.

2. PASS: User can view Epic list with status, progress, next issue.
Evidence: list UI renders grouped cards with delivered/total and next state in `packages/cli/web/src/components/EpicListPage.tsx:97-172`; projected data comes from `packages/cli/src/services/epic-service.ts:69-72,164-214`.

3. PASS: User can open Epic detail with description, progress, next issue, linked issues.
Evidence: detail page renders these sections in `packages/cli/web/src/components/EpicDetailPage.tsx:168-286`.

4. PASS: User can add an existing issue to Epic.
Evidence: API route exists at `packages/cli/src/api/epics.ts:101-159`; detail UI submits add action in `packages/cli/web/src/components/EpicDetailPage.tsx:236-262`; covered by `packages/cli/web/src/components/EpicDetailPage.test.tsx:117-127`.

5. PASS: User can remove an issue from Epic.
Evidence: API route exists at `packages/cli/src/api/epics.ts:161-194`; detail UI remove button is in `packages/cli/web/src/components/EpicDetailPage.tsx:61-85,264-285`; covered by `packages/cli/web/src/components/EpicDetailPage.test.tsx:148-154`.

6. PASS: One issue belongs to at most one primary Epic, with clear duplicate-add feedback.
Evidence: DB uniqueness constraint is in `packages/cli/src/db/migrations.ts:1272-1285`; service translates duplicates in `packages/cli/src/services/epic-service.ts:80-106`; API returns structured duplicate error in `packages/cli/src/api/epics.ts:124-135`; UI formats it clearly in `packages/cli/web/src/components/EpicDetailPage.tsx:87-101,258-262`.

7. PASS: Issue detail shows Epic backlink.
Evidence: issue detail API adds `primaryEpic` in `packages/cli/src/api/issues.ts:1311-1338`; UI renders backlink in `packages/cli/web/src/components/IssueDetailPage.tsx:358-367`.

8. PASS: Epic progress is projected from linked issue state.
Evidence: projection is computed at read time in `packages/cli/src/services/epic-service.ts:146-214`; regression coverage in `packages/cli/tests/epic-regression.test.ts:83-141`.

9. PASS: Next issue ordering is blocked, then active, then backlog, else ready-to-mark-done.
Evidence: ordering logic is in `packages/cli/src/services/epic-service.ts:189-203`; regression coverage in `packages/cli/tests/epic-regression.test.ts:248-355`.

10. PASS: User can mark Epic done or closed; done is not automatic.
Evidence: lifecycle methods only update Epic status in `packages/cli/src/services/epic-service.ts:128-144`; delivered-only progress sets `readyToMarkDone` without changing status in `packages/cli/src/services/epic-service.ts:197-203`; lifecycle tests are in `packages/cli/tests/epic-regression.test.ts:358-417`.

11. PASS: CLI supports `mo epic create/list/show/add-issue/remove-issue/close`.
Evidence: commands are registered in `packages/cli/src/cli/commands/epic.ts:76-307` and wired into the CLI in `packages/cli/src/cli/index.ts:85-90`.

12. PASS: Epic does not appear in Board lanes and cannot be started.
Evidence: Epics live in separate tables in `packages/cli/src/db/migrations.ts:1245-1285`; the web board routes remain issue-only in `packages/cli/web/src/App.tsx:105-120`; there is no Epic start command in `packages/cli/src/cli/commands/epic.ts:69-307`; workflow-isolation tests are in `packages/cli/tests/epic-regression.test.ts:461-489`.

## Review Dimensions

1. Correctness: FAIL because the create API validation response is not structured as required.
2. Complexity: PASS with warning. `packages/cli/src/cli/commands/epic.ts` and `packages/cli/web/src/components/EpicDetailPage.tsx` are larger than the preferred function/component size, but I did not find a direct correctness defect from that alone.
3. Test Coverage: PASS with warning. Epic regression, CLI, and web component tests pass, but invalid-create API behavior is not covered. Verified with `npm test -- epic-regression.test.ts epic-cli.test.ts` and `npm test -- EpicListPage.test.tsx EpicDetailPage.test.tsx` in `packages/cli/`.
4. Security: PASS. I did not find injection or secret-handling issues in the Epic paths reviewed.

<promise>FAIL</promise>
