# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-pr.workflow.yaml:238`
  Evidence: The repair added `check:update-pr` as a normal check-stage task immediately after `ai-review`, but check repair tasks are appended later by the engine after a check fails. `WorkflowRun.Stage.cs:181` appends each repair task to `stage.Tasks` via `stage.Tasks.Add(repairRun)` and `WorkflowRun.Check.cs:107` builds `repairTask` plus optional `verifyTask` for checks. Therefore if `review-passed` fails, the actual order is `ai-review -> check:update-pr -> checks -> fix-review-findings:* -> ai-review.* -> checks`, with no `mohist/create-pull-request` after the repair/verify review. The same applies to `merge-ready` recovery: `rebase-onto-base` is appended after `check:update-pr`, so a successful retry can still merge a PR head that predates the rebase. This still violates acceptance criteria 3/4/6 because final check-stage mutations are not guaranteed to be pushed to the PR before `integrate:merge-pr`. [disallowed:behavior-change]
  SuggestedAction: Ensure every check repair path that can mutate the workspace is followed by an explicit `mohist/create-pull-request` before checks can pass and before integrate merge. One profile-level option is to include a PR update in relevant `repairTask`/`verifyTask` sequences; another is to introduce an explicit post-repair task mechanism if that is the chosen architecture. Do not rely on a static check-stage task placed before checks.
  Verification: Add an engine-level or profile simulation test for `mohist/pr` where `review-passed` fails, `fix-review-findings` and verify `ai-review` run, then assert a `mohist/create-pull-request` task runs after those repair tasks and before integrate. Add the same for `merge-ready` rebase. Re-run the focused server workflow/profile tests.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Profile/MohistPrIssueWorkflowProfileSpecs.cs:284`
  Evidence: `PrWorkflowDefinition_FinalMutatingCheckPathsAreFollowedByPrUpdateBeforeMerge` only asserts the static stage tail is `check:update-pr` and that checks have repair definitions. It does not model `ScheduleCheckRepair`, which appends repairs after the static tail. The test name claims repaired paths are followed by PR update before merge, but the asserted behavior does not prove that runtime order. This allowed item-1 to remain after the attempted repair.
  SuggestedAction: Replace or supplement the static assertion with a workflow runtime test that schedules check repairs and verifies the actual task polling/order includes a PR update after the repair sequence.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter <new-runtime-test-filter>` and the existing profile test class.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: verification
  Evidence: Review verification ran `git diff --check 6ffb9599b..HEAD` and `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter FullyQualifiedName~MohistPrIssueWorkflowProfileSpecs`; both passed. The full server/web/runner test matrix was not run during this review.
  SuggestedAction: Run the full package verification before final integration.
  Status: out-of-scope

<promise>FAIL</promise>
