# Review Report

## Result: FAIL

## Repaired Items

- None.

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx`
  Evidence: The workflow-run YAML trigger is a runtime workflow output (`WorkflowYamlDialog.tsx:15-17` describes it as rendered runtime output), but the current reading flow renders it after the description (`IssueDetailPage.tsx:383-387`) and before comments (`IssueDetailPage.tsx:389-400`). That means one workflow/output block is no longer in the early workflow/output group with `WorkflowView`, `PrDeliverySummary`, and `LatestArtifactsPanel` (`IssueDetailPage.tsx:292-303`). This conflicts with the issue acceptance criterion that workflow progress and artifacts/outputs appear early before description/comments, the design assignment that the `WorkflowYamlDialog` trigger belongs near workflow (`openspec/changes/issue-341/design.md:63-65`), and the reading-flow spec ordering workflow progress and outputs before changes/diff, commits, description, and comments (`openspec/changes/issue-341/specs/issue-detail-reading-flow/spec.md:5-15`). Existing reading-flow tests assert `description < comments`, but they never include `active-run-yaml-trigger` in the ordering assertions, so this regression is not covered. [disallowed:product-behavior]
  SuggestedAction: Move the `WorkflowYamlDialog` trigger into the workflow/output cluster near `WorkflowView`/`LatestArtifactsPanel`, before diff/commits/description, or explicitly revise the spec if runtime YAML is no longer considered workflow output. Add a test asserting `active-run-yaml-trigger` is inside `reading-flow` and precedes `description-section` and `comments-section` when `workflowRunId` exists.
  Verification: After the fix, rerun `npm run typecheck -w packages/web` and `npm run test:run -w packages/web`; the new ordering test should fail on the reviewed snapshot and pass after relocation/spec change.
  Status: open

## Follow-up Items

- None.

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: info
  Scope: `openspec/changes/issue-341/`
  Evidence: The proposal, design, task, spec, progress, self-review, and review artifacts are present under `openspec/changes/issue-341/`. Per the candidate boundary, these are workflow context/evidence and are not product deliverables by themselves.
  SuggestedAction: Leave workflow artifacts in place; do not remove them as part of review repair.
  Status: out-of-scope

- [ID: item-3]
  Severity: info
  Scope: `packages/web/tests/live-task-cloud-event.test.tsx`
  Evidence: The full web test run reports `1 skipped` test; the skipped test is `shows approval toast for legacy approval_requested events` at `packages/web/tests/live-task-cloud-event.test.tsx:311`. This is outside the issue-detail layout change and is not introduced by the current candidate.
  SuggestedAction: Track separately if the skipped legacy cloud-event coverage still matters.
  Status: pre-existing

Verification performed on the reviewed snapshot:

- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 263 files, 4119 tests passed, 1 skipped.

<promise>FAIL</promise>
