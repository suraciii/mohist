# Review Report

## Result: PASS

## Repaired Items

None.

## Blocking Items

None.

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

- [ID: item-1]
  Severity: info
  Scope: verification / package audit
  Evidence: `dotnet test Mohist.sln --filter AgentUsageTimeseriesApiSpecs` invokes the web build as part of the test project and reports `npm audit` findings: 9 vulnerabilities (3 moderate, 3 high, 3 critical). This is existing dependency-audit state surfaced by the build/test pipeline, not introduced by the usage aggregation candidate.
  SuggestedAction: Triage dependency audit findings separately from issue 166.
  Status: pre-existing

## Acceptance Criteria Evidence

- AC1 client snapshot and UI scope label: `packages/web/src/widgets/coder-session/model/usage-snapshot.ts:13` sums `inputTokens`, `outputTokens`, `totalTokens`, and `costAmount` from `useAgentActivity().sessions`; `packages/web/src/pages/activity/ui/ActivityPage.tsx:25` derives the snapshot and `packages/web/src/pages/activity/ui/ActivityPage.tsx:53` renders `UsageSnapshotLabel`; `packages/web/src/widgets/coder-session/ui/UsageSnapshotLabel.tsx:27` visibly labels `activity window only`.
- AC2 server time-bucketed endpoint: `packages/server/src/Mohist.Server/Api/AgentRoutes.cs:40` maps `GET /api/projects/{projectRef}/agent/usage`; `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs:255` returns 7 daily buckets from persisted session usage; `packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuery.cs:42` applies the `CreatedAt` range filter.
- AC3 reviewer-verifiable contract: `openspec/changes/issue-166/specs/agent-usage-aggregation/spec.md:46` documents the endpoint and bucket behavior; DTO fields are declared in `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionReadModels.cs:218`.
- AC4 context isolation: the endpoint lives under the Agent route group in `packages/server/src/Mohist.Server/Api/AgentRoutes.cs:14`, returns only usage bucket DTO fields from `packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionReadModels.cs:224`, and does not share Issue completion endpoints or fields.

## Verification

- `npm run test:run -w packages/web -- ActivityPage.test.tsx UsageSnapshotLabel.test.tsx usage-snapshot.test.ts` passed: 3 files, 18 tests.
- `dotnet test Mohist.sln --filter AgentUsageTimeseriesApiSpecs` passed: 9 tests.

<promise>PASS</promise>
