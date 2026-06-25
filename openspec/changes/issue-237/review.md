# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-pr.workflow.yaml:167`
  Evidence: `build:update-pr` is the last build task, but it runs before build checks and their repair tasks. The same profile then has check-stage review/auto-fix and merge-ready rebase repair paths at `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-pr.workflow.yaml:239`, `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-pr.workflow.yaml:256`, and `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-pr.workflow.yaml:285`, while integrate now merges the previously pushed PR directly at `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-pr.workflow.yaml:318` with no final `create-pull-request` update. If `fix-build-health`, `fix-tests`, `fix-review-findings`, or `rebase-onto-base` changes the workspace after `build:update-pr`, a successful run can merge the stale GitHub PR head and omit locally verified repairs. This violates acceptance criteria 3/4/6: stage results that need remote sync are not guaranteed to be pushed by an explicit update task before checks-gated merge. [disallowed:behavior-change]
  SuggestedAction: Add an explicit PR update after any stage path that can mutate the workspace after `build:update-pr` and before `integrate:merge-pr`, or otherwise move the update to a point that runs after build/check repairs and merge-ready rebase. Add profile tests for build repair, review repair, and merge-ready rebase paths showing that the last mutating step is followed by `mohist/create-pull-request` before merge.
  Verification: Re-run server profile tests and an end-to-end profile simulation where a build/check repair commits after the initial update; assert the merge action sees the updated PR head. Current verification: `npm run typecheck -w packages/runner` passed, but it does not cover this profile sequencing gap.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs:153`
  Evidence: The server profile tests assert the static happy-path ordering `build:open-pr -> load-tasks -> build:update-pr` and `integrate:merge-pr`, but do not cover mutation paths introduced by checks/repair tasks after `build:update-pr`. This let item-1 pass despite the PR no longer being guaranteed to contain the final candidate snapshot. [disallowed:requires behavior test design]
  SuggestedAction: Add regression coverage for repaired build/check paths and merge-ready rebase recovery ensuring an explicit PR update occurs after the repair and before merge.
  Verification: Run `npm test` after adding the profile sequencing tests.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: verification
  Evidence: During review I only ran `npm run typecheck -w packages/runner`, which passed. I did not run the full server/web/runner test matrix, so there may be additional failures outside the inspected sequencing issue.
  SuggestedAction: Run `npm test`, `npm run typecheck -w packages/web`, `npm run test:run -w packages/web`, `npm run typecheck -w packages/runner`, and `npm test -w packages/runner` before integrating.
  Status: out-of-scope

<promise>FAIL</promise>
