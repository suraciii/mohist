# Review Report

## Result: PASS

Acceptance evidence reviewed against issue #260:

- Backend aggregation endpoint exists at `packages/server/src/Mohist.Server/Api/IssueRoutes.ApprovalMetrics.cs:12` and is mapped from `packages/server/src/Mohist.Server/Api/IssueRoutes.cs:20` as `GET /api/projects/{projectRef}/issues/metrics/approval-wait`.
- Backend response shape returns trailing window, `SampleCount`, and nullable `AverageSeconds` / `MedianSeconds` / `MaxSeconds` at `packages/server/src/Mohist.Server/Api/IssueRoutes.Dtos.cs:253-262`.
- Aggregation reads existing workflow approval timestamps by loading workflow state and iterating `MohistDefaultWorkflowProjection.StageApprovals(workflow)` at `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:376-405`; it filters completed `approved` / `rejected` approvals with `RespondedAt`, windows by `respondedAt` in `[now - 7d, now]`, and computes avg / median / max from the same sorted sample set at `IssueQuerier.cs:409-431`.
- `StageApprovals` exposes every stage approval from the workflow status view at `packages/server/src/Mohist.Server/Issue/Services/WorkflowProfiles/MohistDefaultWorkflowProjection.cs:58-75`, while the issue read model still uses the last approval for its single `approvalState` projection at `MohistDefaultWorkflowProjection.cs:34`.
- Server tests cover trailing windowing, stats, single sample, pending exclusion, rejected inclusion, multi-gate counting, zero-sample, zero-duration, project scoping, and API response shape at `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueQuerierSpecs.cs:1021-1250` and `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueMetricsApiSpecs.cs:170-264`.
- Frontend fetches the backend aggregation, not local issue-list data, at `packages/web/src/entities/issue/api/approval-wait.ts:19-41`, and exports the hook/key helpers from `packages/web/src/entities/issue/index.ts:5-6`.
- Attention Hero renders the aggregate average or a defined zero-sample empty state at `packages/web/src/widgets/attention-hero/ui/AttentionHero.tsx:248-269`, including the all-clear Attention area path at `AttentionHero.tsx:272-300`.
- Approval-wait cache refresh is covered for Attention Hero approve, WorkflowView inline approve/request-changes, RuntimeDecisionSurface approve/send-back, and live `StageApprovalResolved` events at `AttentionHero.tsx:59-65`, `WorkflowView.tsx:826-833`, `packages/web/src/entities/issue/api/queries.ts:195-207`, `RuntimeDecisionSurface.tsx:252-270`, and `packages/web/src/app/providers/LiveTaskProvider.tsx:524-528`.

Verification run:

- `git diff --check master...HEAD` passed with no output.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueQuerierSpecs|FullyQualifiedName~IssueMetricsApiSpecs"` passed: 41 passed.
- `npm run typecheck -w packages/web` passed.
- `npm run test:run -w packages/web` passed: 167 files passed; 2414 tests passed, 1 skipped.
- `npm test` passed: server 2840 passed / 14 skipped; web 167 files passed with 2414 passed / 1 skipped; runner 47 files passed / 3 skipped with 650 passed / 23 skipped.

## Repaired Items

_None. No safe local repair was needed during this review._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs`
  Evidence: `GetApprovalWaitAsync` resolves all project issues, loads each workflow run state, and filters approval samples in memory at `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs:376-405`. This matches the existing completion-metrics precedent and is acceptable for this issue, but cost grows with project issue history rather than recent approval count.
  SuggestedAction: Profile large project histories. If this becomes visible, introduce an indexed approval-transition read model or another prefilter that preserves workflow approval semantics.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: branch integration
  Evidence: `git rev-list --left-right --count HEAD...origin/master` reports `9 9`. Missing upstream commits include workflow retry/recovery/artifact changes such as `227f7b24 refactor(workflow): remove server-side recovery fallback logic`, `9d079f98 fix(workflow): detect recovery-only with in retrySelf fallback`, and `69856c2b fix: recovery 匹配与 task status 解耦，artifacts 彻底改为尽力捕获`. This is outside issue #260's approval-wait deliverable, but it is an integration risk for adjacent retry, recovery, and artifact paths.
  SuggestedAction: Rebase or merge latest `origin/master` during integration and rerun workflow/recovery plus approval-wait verification.
  Status: out-of-scope

- [ID: item-3]
  Severity: info
  Scope: approval semantics
  Evidence: The legacy `/reject` route currently calls `RequestChangesAsync` at `packages/server/src/Mohist.Server/Api/IssueRoutes.WorkflowControl.cs:46-62`; `RequestChanges` clears the pending `ApprovalStatus` when adding feedback work at `packages/server/src/Mohist.Server/Workflow/Domain/Run/WorkflowRun.Work.cs:94-98`. Therefore a user-facing "Request changes" / legacy reject action does not become a `rejected` approval sample. The aggregator still correctly supports stored `rejected` approval states, and current specs/tests cover rejected samples directly.
  SuggestedAction: If product later wants request-changes feedback cycles included in human-wait metrics, define a separate contract because that is not the same as the current `approved` / `rejected` completed-approval denominator.
  Status: pre-existing

- [ID: item-4]
  Severity: warning
  Scope: dependency hygiene
  Evidence: Verification passed, but the test/build output reports 9 npm audit vulnerabilities (3 moderate, 3 high, 3 critical), pending `allow-scripts` warnings, and a Vitest 4 deprecation for `test.poolOptions`. These are not introduced by issue #260 and did not fail the candidate verification.
  SuggestedAction: Address audit/allow-scripts and Vitest config warnings in separate maintenance work.
  Status: pre-existing

<promise>PASS</promise>
