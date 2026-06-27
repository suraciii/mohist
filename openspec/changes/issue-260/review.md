# Review Report

## Result: FAIL

Acceptance evidence reviewed against issue #260:

- Backend endpoint exists at `packages/server/src/Mohist.Server/Api/IssueRoutes.ApprovalMetrics.cs:12` and is mapped from `packages/server/src/Mohist.Server/Api/IssueRoutes.cs:20`.
- Backend aggregation uses existing projected approval timestamps from `IssueReadModel.StageApproval` in `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:385-404`, returns avg/median/max/null-empty result in `IssueQuerier.cs:406-428`, and exposes DTO fields in `packages/server/src/Mohist.Server/Api/IssueRoutes.Dtos.cs:253-262`.
- Attention Hero consumes the server metric through `useApprovalWait` in `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:43-47` and renders the value/empty presentation in `AttentionHero.tsx:247-269`.
- Aggregation tests cover main approved, pending, zero-sample, zero-duration, windowing, statistics, single-sample, and project-scope cases in `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueQuerierSpecs.cs:1021-1193`, plus endpoint data/empty cases in `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueMetricsApiSpecs.cs:170-220`.
- Verification run: `git diff --check origin/master...HEAD` produced no output. `npm run typecheck -w packages/web` passed. `npm run test:run -w packages/web` passed with 166 files / 2405 passed / 1 skipped. `npm test` passed with server 2837 passed / 14 skipped, web 166 files / 2405 passed / 1 skipped, runner 47 passed / 3 skipped files and 650 passed / 23 skipped tests.

The candidate still has unresolved freshness and coverage gaps, so the post-repair snapshot cannot pass.

## Repaired Items

_None. No safe local repair was applied; the findings below involve product behavior or non-trivial cross-surface test coverage._

## Blocking Items

- [ID: item-1]
  Severity: warning
  Scope: `packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx`, `packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx`, `packages/web/src/app/providers/LiveTaskProvider.tsx`
  Evidence: The new approval-wait query is cached for 60 seconds with key `['issues','metrics','approval-wait',projectId]` in `packages/web/src/entities/issue/api/approval-wait.ts:24-31`, but only the Attention Hero approve path invalidates its prefix at `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:58-64`. Other existing approval-resolution paths that also change the server aggregate do not invalidate it: WorkflowView approve invalidates only `issues`, `agent-status`, and issue detail keys at `packages/web/src/widgets/issue-workflow/ui/WorkflowView.tsx:826-832`; RuntimeDecisionSurface approve and send-back/reject share `invalidateAll`, which omits approval-wait at `packages/web/src/widgets/issue-workflow/ui/RuntimeDecisionSurface.tsx:251-268`; the global `StageApprovalResolved` live event invalidates only `issues` and `agent-activity` at `packages/web/src/app/providers/LiveTaskProvider.tsx:523-526`. Because the backend intentionally counts both approved and rejected completions at `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:393-404`, using any non-Hero approval/reject path can leave the Dashboard Attention metric stale until the query ages out or the page is refreshed. [disallowed:product-behavior-change]
  SuggestedAction: Centralize approval-wait invalidation and call it from every approval-resolution mutation path, especially WorkflowView approve, RuntimeDecisionSurface approve/reject, and the `StageApprovalResolved` live event handler. Add regression coverage proving the approval-wait query is invalidated after approve and reject outside the Hero.
  Verification: Run `npm run test:run -w packages/web`; manually approve and reject from issue detail/runtime surfaces, return to Dashboard within 60 seconds, and confirm the Attention Hero refetches the approval-wait metric.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueQuerierSpecs.cs`, `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueMetricsApiSpecs.cs`
  Evidence: The issue and spec require completed approvals with `approvalState.status` of `approved` or `rejected` to participate in the denominator. The implementation checks both strings in `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:393-394`, but the new aggregation tests exercise only the default approved path: `ApprovalRunState(..., result = "approved")` defaults at `IssueQuerierSpecs.cs:1265-1266`, and the added tests at `IssueQuerierSpecs.cs:1021-1193` do not pass `"rejected"`; the endpoint data-present seed in `IssueMetricsApiSpecs.cs:178-185` also uses `"approved"`. A future change could silently drop rejected approvals while all new tests still pass.
  SuggestedAction: Add at least one service-level aggregation test, and preferably an endpoint contract test, where a rejected approval contributes a wait sample exactly like an approved approval.
  Verification: Run `npm test` or a targeted `dotnet test` filter for `IssueQuerierSpecs` / `IssueMetricsApiSpecs`.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: `packages/web/src/entities/issue/api/approval-wait.ts`
  Evidence: The task acceptance criteria require `useApprovalWait()` to use query key `['issues','metrics','approval-wait',projectId]`, `staleTime: 60_000`, and `enabled: !!projectId`. The hook implements that at `packages/web/src/entities/issue/api/approval-wait.ts:24-31`, but there is no corresponding hook/API contract test (`packages/web/src/entities/issue/api/approval-wait.test.ts` does not exist). The sibling completion metric has explicit hook tests in `packages/web/src/entities/issue/api/completion-trend.test.ts:23-110`, so this new public query contract is unpinned.
  SuggestedAction: Add an `approval-wait.test.ts` mirroring `completion-trend.test.ts`, covering query key scoping, enabled/disabled behavior, stale time, and the fetch path `/api/projects/{id}/issues/metrics/approval-wait`.
  Verification: Run `npm run test:run -w packages/web`.
  Status: open

- [ID: item-4]
  Severity: minor
  Scope: `packages/web/src/shared/lib/format-duration.ts`
  Evidence: The planned formatter contract in `openspec/changes/issue-260/design.md:99` and `openspec/changes/issue-260/tasks.json:33` gives examples `"3.2h"`, `"5d"`, and `"<1m"`. The implementation formats all day values below 10 days with one decimal at `packages/web/src/shared/lib/format-duration.ts:9-11`, and the test now expects `formatDuration(86_400 * 5)` to be `"5.0d"` at `packages/web/src/shared/lib/format-duration.test.ts:29-33`. This is less compact than the stated UI contract and can surface as `Your approvals averaged 5.0d` in the Hero. [disallowed:product-behavior-change]
  SuggestedAction: Align the formatter and tests with the compact examples, for example by dropping a trailing `.0` for whole day values.
  Verification: Run `npm run test:run -w packages/web` and inspect the Attention Hero for day-scale values.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`
  Evidence: `GetApprovalWaitAsync` loads every issue row for the project, maps each issue, loads workflow run states, and deserializes/project them before filtering samples in memory at `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:376-404`. This follows the documented completion-metrics precedent and is acceptable for the current scope, but it will scale with project issue count rather than with recent approvals.
  SuggestedAction: Keep the current implementation for v1, but profile projects with large issue histories. If it becomes visible, consider a stored/indexed approval transition read model or a safe JSON prefilter that does not duplicate profile selection semantics.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: warning
  Scope: branch integration
  Evidence: The worktree is clean, but the review branch is ahead 7 and behind `origin/master` by 1. The missing upstream commit is `69856c2b fix: recovery matching and task status decoupling, artifacts best-effort capture`, touching recovery/artifact paths outside this candidate's approval-wait deliverable. This is not a defect in the reviewed code, but it is an integration risk to resolve before merge.
  SuggestedAction: Rebase or merge the latest `origin/master` during integration and rerun the affected verification.
  Status: out-of-scope

- [ID: item-7]
  Severity: info
  Scope: test configuration
  Evidence: Vitest emits `DEPRECATED test.poolOptions was removed in Vitest 4` during `npm test` and `npm run test:run -w packages/web`. The tests still pass, and this warning is not introduced by the approval-wait change.
  SuggestedAction: Clean up the Vitest configuration in a separate maintenance change.
  Status: pre-existing

<promise>FAIL</promise>
