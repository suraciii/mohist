# Review Report

## Result: PASS

## Repaired Items

(none)

## Blocking Items

(none)

## Follow-up Items

(none)

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: info
  Scope: packages/web/vitest configuration
  Evidence: `npm run test:run -w packages/web` passes, but Vitest prints: ``DEPRECATED  `test.poolOptions` was removed in Vitest 4. All previous `poolOptions` are now top-level options.`` This warning is pre-existing/out of scope for the create-issue toast fix and does not affect the candidate behavior.
  SuggestedAction: Update the Vitest config in a separate cleanup change.
  Status: pre-existing

## Acceptance Criteria Evidence

- Successful create shows the concrete issue number: `packages/web/src/features/create-issue/ui/CreateIssueDialog.tsx:201` receives the bare `Issue` response and `CreateIssueDialog.tsx:202` emits `toast.success(`Issue #${data.number} created`)`. Test coverage at `packages/web/src/features/create-issue/ui/CreateIssueDialog.test.tsx:142` mocks `{ id: 'issue_223', number: 223 }` and asserts `Issue #223 created` at `CreateIssueDialog.test.tsx:150`.
- Success toast does not render `undefined`: `CreateIssueDialog.tsx:202` reads required `Issue.number`; the model requires `number: number` at `packages/web/src/entities/issue/model/issue.ts:81`. Tests assert no `undefined` in success messages at `CreateIssueDialog.test.tsx:152` and `CreateIssueDialog.test.tsx:169`.
- The success handler reads from the bare create response, not a `{ issue }` wrapper: `packages/web/src/entities/issue/api/client.ts:21` returns `request<Issue>` for `createIssue`, while sibling mutation helpers such as `startIssue` and `closeIssue` return wrappers at `client.ts:51` and `client.ts:55`. The candidate uses `data.number` at `CreateIssueDialog.tsx:202`; test coverage at `CreateIssueDialog.test.tsx:156` pins this against a bare response.
- Failed create surfaces a number-free error toast: `CreateIssueDialog.tsx:206` handles mutation errors with `toast.error(err.message || 'Failed to create issue')`. Tests assert the rejection message at `CreateIssueDialog.test.tsx:172` and the generic fallback at `CreateIssueDialog.test.tsx:187`, including checks that the error toast contains no `undefined` or issue-number pattern.
- No API contract change was introduced: the only product files changed are `CreateIssueDialog.tsx` and `CreateIssueDialog.test.tsx`; `createIssue` still returns a bare `Issue` from `packages/web/src/entities/issue/api/client.ts:21`.
- Toast infrastructure exists in the app shell: `packages/web/src/app/App.tsx:20` imports `Toaster` from `sonner` and `App.tsx:80` renders `<Toaster />`.

## Verification

- `git diff --check master...HEAD` passed.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 170 test files passed, 2431 tests passed, 1 skipped.
- Related search confirmed there is only one product create-issue caller: `packages/web/src/features/create-issue/ui/CreateIssueDialog.tsx:187`.

<promise>PASS</promise>
