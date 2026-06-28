# Review Report

## Result: FAIL

## Repaired Items

- (none)

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: packages/web/src/entities/coder-session/model/useWorkflowRunSessions.ts; packages/web/src/widgets/issue-workflow/model/useSiblingSessions.ts; packages/web/src/pages/session/ui/SessionPage.tsx
  Evidence: `useWorkflowRunSessions` keeps `liveSessions` from the previous workflow run until the new query finishes because the sync effect returns early while `isLoading` is true and never clears on `workflowRunId` changes (`useWorkflowRunSessions.ts:19-25`). Both `WorkflowSessionsPanel` and `SessionPage` render from that hook (`WorkflowSessionsPanel.tsx:255-257`; `useSiblingSessions.ts:21-24`; `SessionPage.tsx:591-736`). During a workflow-run switch, issue change, rerun, or refetch where a different `workflowRunId` is loading, the panel/sidebar can temporarily show and link to sessions from the prior run. This violates the acceptance criterion that session navigation stays within the current issue/session set and that the sidebar matches the same workflow run's panel set. [disallowed:reason] Repair changes live-data behavior and needs a product decision on loading vs. empty state.
  SuggestedAction: Reset `liveSessions` when `workflowRunId` changes or key local state by workflowRunId so old sessions are not rendered for a new run. Add hook/component tests that render with `wr-1` sessions, switch to `wr-2` while loading, and assert no `wr-1` rows/sidebar links remain.
  Verification: `npm run typecheck -w packages/web` passed; `npm run test:run -w packages/web` passed (183 files, 2718 passed, 1 skipped); `npm test` passed (dotnet plus workspace tests, 50 runner files passed / 3 skipped, 713 passed / 23 skipped). Existing tests do not cover workflowRunId changes while loading.
  Status: unresolved

- [ID: item-2]
  Severity: test-gap
  Scope: packages/web/tests/e2e/workflow-sessions-responsive.spec.ts; packages/web/src/widgets/issue-workflow/ui/WorkflowSessionsPanel.test.tsx
  Evidence: The narrow-container acceptance criterion is only partially verified. The jsdom tests assert Tailwind class names (`WorkflowSessionsPanel.test.tsx:263-382`), and the Playwright test constructs standalone HTML/CSS with copied class behavior instead of rendering `WorkflowSessionsPanel` (`workflow-sessions-responsive.spec.ts:5-43`). That browser test would still pass if the real React component lost a key class, generated a different DOM, or Tailwind output changed, so it does not verify the post-build candidate snapshot for actual overflow/wrapping behavior. [disallowed:reason] Repair requires adding an integrated browser test harness or changing test strategy, not a safe local review fix.
  SuggestedAction: Replace or supplement the static HTML Playwright case with an actual app/component render of `WorkflowSessionsPanel` populated with long session names, long model labels, many metric chips, and failure text, then assert no horizontal scroll and visible key content at narrow widths.
  Verification: `npm run test:run -w packages/web` passed, but the responsive browser test does not exercise product code.
  Status: unresolved

## Follow-up Items

- [ID: item-3]
  Severity: follow-up
  Scope: openspec/changes/issue-244/proposal.md; openspec/changes/issue-244/design.md
  Evidence: The proposal/design state this is a web-only change with no server/API changes (`proposal.md:31-33`, `design.md:20-23`), but the candidate adds `stage`, terminal status, `completedAt`, failure reason, and exit code behavior to the workflow sessions DTO (`AgentSessionReadModels.cs:159-179`; `AgentSessionQuerier.cs:39-61`, `63-73`, `564-582`, `657-686`). The product change looks justified for status filtering and duration sorting, and it is covered by server tests, but the design artifact is now stale.
  SuggestedAction: Update the design/proposal impact section so future reviewers know the API/read-model change is intentional and can trace it to the filtering/sorting requirements.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- (none)

<promise>FAIL</promise>
