# Review Report

## Result: FAIL

The implementation covers the primary acceptance criteria: both error branches converge at `packages/web/src/pages/issue-changed-files/ui/IssueChangedFilesPage.tsx:794-819`; issue context and product-language messages render at `:158-203`; retry re-fetches all three queries at `:811-815`; the return action is present at `:213-220`; and the session action uses the established route at `:779-788` and `:222-231`.

`npm run typecheck -w packages/web`, `npm run test:run -w packages/web` (334 files, 4675 tests), and `npm run test:ci -w packages/web` all passed. No security, data-safety, public-contract, or migration defect was found. The unresolved coverage gaps below prevent a passing verdict.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: formatting
  Evidence: `openspec/changes/issue-403/progress.txt` ended with a superfluous blank line, causing `git diff --check master...HEAD` to report `new blank line at EOF`.
  Verification: Removed the blank line and ran `git diff --check master` successfully.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/web/src/pages/issue-changed-files/ui/IssueChangedFilesPage.recovery.test.tsx`
  Evidence: The server can independently return a semantic unavailable response from `/commits` (`packages/server/src/Mohist.Server/Api/WorkspaceRoutes.cs:76-128`), and the new code explicitly handles `commitsData.available === false` (`packages/web/src/pages/issue-changed-files/ui/IssueChangedFilesPage.tsx:655-660`). Every semantic-unavailability test only sets `diffData` (`IssueChangedFilesPage.recovery.test.tsx:14-54`); commits are covered only as an HTTP error (`:115-122`).
  SuggestedAction: Add a scenario with an available diff and an unavailable commits response, asserting the shared recovery surface, the mapped message, and its actions.
  Verification: Run `npm run test:run -w packages/web -- IssueChangedFilesPage.recovery.test.tsx`.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/web/src/pages/issue-changed-files/ui/IssueChangedFilesPage.recovery.test.tsx`
  Evidence: The retry-success test claims to verify all three refetches but only observes that the diff view becomes visible after changing fixture data (`:136-168`). The fixture records no issue, diff, or commits request counts (`IssueChangedFilesPage.fixture.tsx:178-205`). The persistent-failure assertion also succeeds immediately against the pre-existing DOM node (`IssueChangedFilesPage.recovery.test.tsx:171-181`), before it demonstrates that the retried requests settled and the recovery actions remain.
  SuggestedAction: Add request counters or MSW spies for all three evidence endpoints, assert each is called after Retry, and wait for the persistent-failure retry responses before checking the surface and actions.
  Verification: Run `npm run test:run -w packages/web -- IssueChangedFilesPage.recovery.test.tsx`.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: `packages/web/src/pages/issue-changed-files/ui/IssueChangedFilesPage.recovery.test.tsx`
  Evidence: The session-absence cases assert that the action is absent immediately after the recovery surface appears (`:238-268`). They do not wait for the sessions request to resolve, so they pass while `useWorkflowRunSessions` is still loading regardless of the response. The no-workflow-run case likewise does not assert that the sessions endpoint was not called. The hook's gate is correct (`packages/web/src/entities/coder-session/model/useWorkflowRunSessions.ts:22-27`), but this acceptance criterion is not protected by the new tests.
  SuggestedAction: Instrument the session MSW handler, assert zero calls when the issue has no workflow run ID, and assert absence only after an empty or terminal-only sessions response has resolved.
  Verification: Run `npm run test:run -w packages/web -- IssueChangedFilesPage.recovery.test.tsx`.
  Status: open

- [ID: item-5]
  Severity: test-gap
  Scope: `packages/web/src/pages/issue-changed-files/ui/IssueChangedFilesPage.fixture.tsx`
  Evidence: The fixture provides `initialProjectId` but no project definition (`:229`), so `currentProject` is null (`packages/web/src/entities/project/model/ProjectContext.tsx:57`) and `useProjectPath` deliberately produces unprefixed paths (`:68-76`). The navigation assertions therefore only test `/issues/123` and `/issues/123/workflow/sessions/...` (`IssueChangedFilesPage.recovery.test.tsx:192-200`, `:215-216`), while production routes are project-scoped (`packages/web/src/app/App.tsx:62-68`).
  SuggestedAction: Add a recovery fixture with a named current project and verify that both recovery navigation actions preserve the encoded project-name route prefix.
  Verification: Run `npm run test:run -w packages/web -- IssueChangedFilesPage.recovery.test.tsx`.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

None.

<promise>FAIL</promise>
