# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: packages/web/src/widgets/issue-workflow/ui/milestones.ts
  Evidence: Issue #339 acceptance criteria require agent-task milestone eligibility to use `origin.uses` / `sessionName` / `classification`, and the delta spec says agent tasks are identified by those trusted task-level fields. `TaskProgressPanel` forwards `classification` into `TaskLogPanel` (`packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.tsx:178-180`), and `TaskLogPanel` passes it into `isAcpAgentTask` (`packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.tsx:161`), but `isAcpAgentTask` never reads it (`packages/web/src/widgets/issue-workflow/ui/milestones.ts:26-33`). The current tests encode the opposite behavior: missing classification is accepted as eligible (`packages/web/src/widgets/issue-workflow/ui/milestones.test.ts:40-44`, `packages/web/src/widgets/issue-workflow/ui/TaskLogPanel.test.tsx:1463-1485`). That means a task with `origin.uses === 'mohist/acp-agent'` and a non-empty `sessionName` still renders milestones even when the required classification signal is absent. [disallowed:product-behavior-change]
  SuggestedAction: Decide the intended classification rule and make implementation, specs, and tests agree. If the issue acceptance criteria are authoritative, require an expected classification value (for example `UserFacing`, if that is the intended agent-task classification) in `isAcpAgentTask`, and update the tests that currently assert classification is optional.
  Verification: Add/adjust tests so missing or non-agent classification does not render milestones, then run `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, and the focused task-log a11y test.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: packages/web/src/widgets/issue-workflow/ui/TaskProgressPanel.test.tsx
  Evidence: The issue requires the `TaskProgressPanel` timeline projection to preserve `sessionName`, `origin.uses`, and `classification` and forward them into `TaskLogPanel`. The implementation does this in `TaskProgressPanel.tsx:238-254` and `TaskProgressPanel.tsx:172-181`, but no changed test exercises the actual parent-to-panel path. The existing `TaskProgressPanel.test.tsx` fixtures do not include the new `mohist/acp-agent` + `sessionName` + `classification` path, and the candidate did not modify that test file. Most milestone assertions instantiate `TaskLogPanel` directly, so a future regression in the timeline projection or prop forwarding could pass the new milestone tests while breaking the user-visible task panel.
  SuggestedAction: Add a `TaskProgressPanel` test with a timeline task carrying `uses: 'mohist/acp-agent'`, a non-empty `sessionName`, and `classification`, mock the workflow-run sessions data, expand the task row, and assert the model-bound/session-ended milestones render through the real parent path.
  Verification: Run `npm run test:run -w packages/web -- TaskProgressPanel.test.tsx` or the full `npm run test:run -w packages/web` after adding the coverage.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: packages/web/tests/a11y/settings.a11y.spec.ts
  Evidence: `npm run test:a11y -w packages/web` fails in the unrelated Settings browser audit, not in the task-log milestone row tests. The failures include existing color-contrast violations such as `.text-primary/80` on white at contrast ratio `3.76` under multiple Settings routes, a dark-mode `text-red-700` contrast failure at ratio `2.94`, plus two Settings workflow-tab timeouts/missing-element failures. This candidate did not change Settings files, and the focused task-log a11y command passed: `npx vitest run --config vitest.a11y.config.ts tests/a11y/task-log-a11y.test.tsx` reported `1` file / `6` tests passed.
  SuggestedAction: Track the Settings a11y failures separately, then rerun the full a11y gate.
  Status: pre-existing

<promise>FAIL</promise>
