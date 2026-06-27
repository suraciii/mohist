# Review Report

## Result: FAIL

Acceptance evidence reviewed against issue #260:

- Backend endpoint exists at `packages/server/src/Mohist.Server/Api/IssueRoutes.ApprovalMetrics.cs:12` and is mapped from `packages/server/src/Mohist.Server/Api/IssueRoutes.cs:20`.
- Backend aggregation currently builds projected issue read models and samples `IssueReadModel.StageApproval` in `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:376-404`, returning nullable avg/median/max and `SampleCount` in `IssueQuerier.cs:406-428` through DTOs at `packages/server/src/Mohist.Server/Api/IssueRoutes.Dtos.cs:253-262`.
- Attention Hero consumes `useApprovalWait` in `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:42-48` and renders value/empty states in `AttentionHero.tsx:248-269`.
- Approval-wait cache invalidation is now centralized in `packages/web/src/entities/issue/api/approval-wait.ts:25-32` and is called from the Hero approve path, WorkflowView inline approve, RuntimeDecisionSurface approve/send-back, `useRequestChangesIssue`, and the live `StageApprovalResolved` event.
- Verification run: `git diff --check master...HEAD` produced no output. `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueQuerierSpecs|FullyQualifiedName~IssueMetricsApiSpecs"` passed 39 tests. `npm run typecheck -w packages/web` passed. Focused web tests passed 7 files / 98 tests. Full `npm test` passed: server 2837 passed / 14 skipped, web 166 files / 2405 passed / 1 skipped, runner 47 passed / 3 skipped files and 650 passed / 23 skipped tests.

The post-build snapshot still has a product-contract correctness issue in the aggregation denominator, so the verdict is FAIL.

## Repaired Items

_None. No safe local repair was applied; the remaining issue requires product behavior and test changes._

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`, `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/MohistDefaultWorkflowProjection.cs`, `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueQuerierSpecs.cs`
  Evidence: Issue #260 defines the metric as every issue approval from `requestedAt` to `respondedAt`, and the delta spec says the system SHALL measure approval waiting time "per completed approval" at `openspec/changes/issue-260/specs/approval-waiting-metrics/spec.md:3-5`. The default workflow has two approval gates, Plan and Check, at `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/mohist-local.workflow.yaml:23-25` and `mohist-local.workflow.yaml:190-191`. However `GetApprovalWaitAsync` samples only `issue.StageApproval` at `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:390-403`. That field is the projection's single selected approval, because `MohistDefaultWorkflowProjection.ProjectWorkflowState` maps all stage approvals and then takes `.LastOrDefault()` at `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/MohistDefaultWorkflowProjection.cs:34-43`. A workflow run with both Plan and Check approvals inside the trailing 7 days contributes one sample instead of two, undercounting `SampleCount` and skewing avg/median/max. Existing new tests only seed one approval stage per workflow run through `ApprovalRunState` / `RunState` at `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueQuerierSpecs.cs:1289-1329`, so this denominator bug is not covered. [disallowed:product-behavior-change]
  SuggestedAction: Aggregate over every completed approval gate in the workflow run state, or explicitly change the product/spec contract to "the projected approval for an issue" if only the last gate is intended. Add a regression test with one workflow run containing both completed Plan and Check approvals in the trailing window and assert `SampleCount == 2` with stats computed from both samples. Add an endpoint-level variant if the API contract should pin this behavior.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueQuerierSpecs|FullyQualifiedName~IssueMetricsApiSpecs"` and `npm test` after the fix.
  Status: open

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`
  Evidence: `GetApprovalWaitAsync` loads every issue row for the project, deserializes workflow run state, applies projections, then filters samples in memory at `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:376-404`. This follows the existing completion-metrics precedent and is acceptable for v1, but cost scales with project issue count rather than recent approval count.
  SuggestedAction: Keep the current approach for this issue, but profile large project histories. If it becomes visible, consider a stored/indexed approval transition read model or a safe prefilter that does not drift from workflow semantics.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: warning
  Scope: branch integration
  Evidence: The worktree is clean, but `git rev-list --left-right --count HEAD...origin/master` reports `8 9`. The missing upstream commits include workflow recovery/artifact changes such as `227f7b24 refactor(workflow): remove server-side recovery fallback logic`, `9d079f98 fix(workflow): detect recovery-only with in retrySelf fallback`, and `69856c2b fix: recovery 匹配与 task status 解耦，artifacts 彻底改为尽力捕获`. This is not caused by the approval-wait deliverable, but it is an integration risk for adjacent retry, recovery, and artifact paths.
  SuggestedAction: Rebase or merge the latest `origin/master` during integration and rerun affected workflow/recovery tests plus the approval-wait checks.
  Status: out-of-scope

- [ID: item-4]
  Severity: warning
  Scope: dependency hygiene
  Evidence: The full `npm test` run completed successfully, but the server test build's npm audit step reported 9 vulnerabilities (3 moderate, 3 high, 3 critical) and pending allow-scripts warnings. Vitest also emitted `DEPRECATED test.poolOptions was removed in Vitest 4`. These are not introduced by issue #260 and did not fail verification.
  SuggestedAction: Address audit/allow-scripts and Vitest config warnings in separate maintenance work.
  Status: pre-existing

<promise>FAIL</promise>
