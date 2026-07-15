# Review Report

## Result: FAIL

The post-repair candidate implements the unified Files/Diff recovery surface, product-language messages, retry, issue navigation, and project-scoped active-session navigation. It does not meet the issue acceptance criterion requiring a related-session action whenever a session is known: terminal sessions are deliberately excluded.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: test organization
  Evidence: `IssueChangedFilesPage.recovery.test.tsx` grew to 330 lines, exceeding the repository's 300-line test-file budget and causing `npm test` to fail in `check:test-boundaries`. Moved the five related-session scenarios to `IssueChangedFilesPage.recovery-session.test.tsx`; the files are now 245 and 94 lines respectively.
  Verification: `npm run typecheck -w packages/web`; `npm run test:run -w packages/web -- IssueChangedFilesPage.recovery.test.tsx IssueChangedFilesPage.recovery-session.test.tsx` (25 passed); `npm test` (all server, CLI, Web, and Runner checks passed); `git diff --check`.
  Status: resolved

## Blocking Items

- [ID: item-2]
  Severity: blocking
  Scope: `packages/web/src/pages/issue-changed-files/ui/IssueChangedFilesPage.tsx:781`
  Evidence: The page only selects sessions whose status is `active`, `running`, or `probing` (`:781-785`), so a known `completed`, `failed`, or `cancelled` workflow-run session produces no recovery action. Those are valid terminal session states (`packages/web/src/widgets/issue-workflow/model/useWorkflowSessionFiltering.ts:16-20`) and the session route has no live-status restriction (`packages/web/src/app/App.tsx:68`). The new test explicitly locks the exclusion in `IssueChangedFilesPage.recovery-session.test.tsx:77-90`. This violates issue acceptance criterion 5: when a related session is known, the user can open it. [disallowed: product behavior change]
  SuggestedAction: Render a session action for a known terminal session as well, with an explicit deterministic selection rule when multiple sessions exist; retain preference for a live session if that is the intended UX. Update the terminal-session scenario to assert navigation to its session route.
  Verification: Add completed, failed, and cancelled session fixtures to `IssueChangedFilesPage.recovery-session.test.tsx`, then verify each renders and navigates through `/issues/:number/workflow/sessions/:sessionName`.
  Status: unresolved

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

None.

<promise>FAIL</promise>
