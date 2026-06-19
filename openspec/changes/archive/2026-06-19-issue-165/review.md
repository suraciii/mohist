# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: missing-obvious-guards
  Evidence: `packages/web/src/entities/issue/lib/completion-snapshot.ts` added the standalone `deriveCompletionSnapshot()` and `useCompletionSnapshot()` reservation hook, but `packages/web/src/entities/issue/index.ts` did not export them from the issue entity boundary. That made the documented stable location harder for downstream dashboard code to consume. Added exports for `deriveCompletionSnapshot`, `useCompletionSnapshot`, and `CompletionSnapshot` in `packages/web/src/entities/issue/index.ts`.
  Verification: `npm run test:run -w packages/web -- completion-snapshot`; `npm run build -w packages/web`; `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~IssueQuerierSpecs|FullyQualifiedName~IssueMetricsApiSpecs"`
  Status: resolved

## Blocking Items

None.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: repository dependency audit
  Evidence: The focused server test command triggers the web production build and npm audit output reports 9 vulnerabilities (3 moderate, 3 high, 3 critical). This is unrelated to the completion metrics change and no vulnerable package was introduced in the reviewed candidate.
  SuggestedAction: Track dependency audit remediation separately with ownership for package upgrades and regression testing.
  Status: out-of-scope

## Acceptance Criteria Evidence

- Client snapshot: `packages/web/src/entities/issue/lib/completion-snapshot.ts` computes `{ completed, failed, new }` from loaded issues using `status`, `createdAt`, and `updatedAt` only; tests in `packages/web/src/entities/issue/lib/completion-snapshot.test.ts` cover in-window counts, boundary exclusion, non-terminal exclusion, created-at new counts, hook shape, and no-fetch purity.
- Server endpoint: `packages/server/src/Mohist.Server/Api/IssueRoutes.Metrics.cs` exposes `GET /api/projects/{projectRef}/issues/metrics/completion?bucket=day|week`, rejects unsupported bucket values, and returns dense day/week windows with `{ boundary, completed, failed }` buckets.
- Completion-time semantics: `packages/server/src/Mohist.Server/Issue/Services/IssueQuerier.cs` buckets terminal `IssueEvents` rows by `Time`, not issue `updatedAt`, and tests in `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Querier/IssueQuerierSpecs.cs` plus `packages/server/tests/Mohist.Server.Tests/Specs/Issue/Api/IssueMetricsApiSpecs.cs` cover edited-after-completion attribution, project scoping, fixed windows, non-terminal event exclusion, and distinct-per-bucket behavior.
- AgentActivity exclusion: searched the reviewed implementation paths and found no use of `AgentActivity.summary.completed` or `AgentActivity.summary.failed`; the metric code uses issue timestamps and `IssueEvents` terminal event facts.

<promise>PASS</promise>
