# Review Report

## Result: PASS

## Repaired Items

_None._

## Blocking Items

_None._

## Follow-up Items

- [ID: item-1]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Api/AgentRoutes.cs, packages/server/src/Mohist.Server/Runner/Services/RunnerStatusService.cs
  Evidence: `AgentRoutes.SumCapacity` sums the same `RunnerStatusView.Capacity` values that `RunnerStatusService.GetCapacityAsync` also sums. This is not currently incorrect because `/agent/status` intentionally computes the top-level capacity from the exact runner views returned by `GetOnlineRunnersAsync`, so `Capacity` and `Runners[]` are internally consistent. It is still a small drift risk if the capacity fold ever gains extra rules.
  SuggestedAction: If this logic changes again, move the fold behind a shared helper on `RunnerStatusService` so `/agent/status` can derive the aggregate from its already-loaded runner views without duplicating the loop.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-2]
  Severity: warning
  Scope: packages/web/src/pages/agent-list/ui/AgentListPage.tsx, packages/web/src/app/App.tsx, packages/web/src/pages/agent-detail/ui/AgentDetailPage.tsx
  Evidence: The broader post-build snapshot includes unrelated Agent workbench changes where `AgentListPage` navigates "New Agent" to `/agents/new` ([AgentListPage.tsx:141](../../../packages/web/src/pages/agent-list/ui/AgentListPage.tsx#L141), [AgentListPage.tsx:151](../../../packages/web/src/pages/agent-list/ui/AgentListPage.tsx#L151)), but the router only registers `agents` and `agents/:agentId` ([App.tsx:68](../../../packages/web/src/app/App.tsx#L68), [App.tsx:69](../../../packages/web/src/app/App.tsx#L69)).
  Evidence: That means `/agents/new` is interpreted as agent id `new`, and `AgentDetailPage` calls `useAgent(agentId ?? '')` ([AgentDetailPage.tsx:90](../../../packages/web/src/pages/agent-detail/ui/AgentDetailPage.tsx#L90)). This does not affect issue-300 runner capacity behavior and appears to belong to the unrelated Agent workbench snapshot.
  SuggestedAction: Add a real create-agent route or open the profile editor directly from the list page in the Agent workbench change set.
  Status: out-of-scope

- [ID: item-3]
  Severity: info
  Scope: issue-300 acceptance criteria
  Evidence: Issue ACs are satisfied in the reviewed product snapshot. `/agent/status` now reads runner views from `RunnerStatusService.GetOnlineRunnersAsync` and computes top-level capacity from those views, not from `activeAgents` ([AgentRoutes.cs:16](../../../packages/server/src/Mohist.Server/Api/AgentRoutes.cs#L16), [AgentRoutes.cs:19](../../../packages/server/src/Mohist.Server/Api/AgentRoutes.cs#L19), [AgentRoutes.cs:21](../../../packages/server/src/Mohist.Server/Api/AgentRoutes.cs#L21), [AgentRoutes.cs:129](../../../packages/server/src/Mohist.Server/Api/AgentRoutes.cs#L129), [AgentRoutes.cs:141](../../../packages/server/src/Mohist.Server/Api/AgentRoutes.cs#L141)).
  Evidence: `/agent/activity.summary.slots` receives capacity from `RunnerStatusService.GetCapacityAsync` through the route and projects that supplied runner capacity ([AgentRoutes.cs:32](../../../packages/server/src/Mohist.Server/Api/AgentRoutes.cs#L32), [AgentRoutes.cs:35](../../../packages/server/src/Mohist.Server/Api/AgentRoutes.cs#L35), [AgentSessionQuerier.cs:692](../../../packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs#L692), [AgentSessionQuerier.cs:714](../../../packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs#L714)).
  Evidence: The authoritative runner projection counts distinct workflow owner ids and persisted slots ([RunnerStatusService.cs:136](../../../packages/server/src/Mohist.Server/Runner/Services/RunnerStatusService.cs#L136), [RunnerStatusService.cs:144](../../../packages/server/src/Mohist.Server/Runner/Services/RunnerStatusService.cs#L144)). `activeAgents` remains visible-session data and the issue detail Start gate now uses server `capacity.active >= capacity.max` instead of `activeAgents.length` ([IssueDetailPage.tsx:407](../../../packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx#L407), [IssueDetailPage.tsx:410](../../../packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx#L410), [IssueDetailPage.tsx:1283](../../../packages/web/src/pages/issue-detail/ui/IssueDetailPage.tsx#L1283)).
  Evidence: Divergence coverage exists for `/agent/status`, `/agent/activity`, and the web gate ([RuntimeEntrySpecs.cs:222](../../../packages/server/tests/Mohist.Server.Tests/Specs/SystemSpecs/RuntimeEntrySpecs.cs#L222), [AgentSessionSpecs.cs:919](../../../packages/server/tests/Mohist.Server.Tests/Specs/Sessions/AgentSessionSpecs.cs#L919), [IssueDetailPage.capacity-gating.test.tsx:112](../../../packages/web/src/pages/issue-detail/ui/IssueDetailPage.capacity-gating.test.tsx#L112), [IssueDetailPage.capacity-gating.test.tsx:154](../../../packages/web/src/pages/issue-detail/ui/IssueDetailPage.capacity-gating.test.tsx#L154)).
  SuggestedAction: None.
  Status: out-of-scope

- [ID: item-4]
  Severity: info
  Scope: verification
  Evidence: Verification passed for the reviewed snapshot: `npm test` completed successfully, including `dotnet test Mohist.sln -p:SkipWebBuild=true` and workspace `test:ci`; `npm run typecheck -w packages/web` completed successfully; `npm run test:run -w packages/web` completed with 223 test files passed and 3363 tests passed, 1 skipped. `git show --check 4a8f0592 1a5dddae 043a1dab 15aa91d7` reported no whitespace errors.
  SuggestedAction: None.
  Status: out-of-scope

<promise>PASS</promise>
