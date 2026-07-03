# Review Report

## Result: PASS

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: typos
  Evidence: `packages/server/src/Mohist.Server/Sessions/AgentSessionReadModels.cs` still had an XML doc reference to the removed `AgentSessionQuerier.GetCostWindowedAsync` on the `AgentCostWindowedData` comment. The implementation moved that method to `AgentUsageReporter`; the stale reference was updated to `Sessions.Services.AgentUsageReporter.GetCostWindowedAsync`.
  Verification: `git grep -n "AgentSessionQuerier\.GetCostWindowedAsync\|AgentSessionQuerier\.GetUsageTimeseriesAsync\|AgentSessionQuerier\.GetCostRollupAsync\|AgentSessionQuerier\.GetActivityAsync" -- packages/server/src/Mohist.Server packages/server/tests` returned no matches. `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter "FullyQualifiedName~Sessions"` passed: 295 passed, 2 skipped.
  Status: resolved

## Blocking Items

_None._

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: `packages/web/src/entities/session/model/view.ts`
  Evidence: The server-side session chain now uses only `session.closed`, but the web session view still accepts both spellings in `isSessionClosedEvent` (`type === 'session.closed' || type === 'session_closed'` at line 210), and adjacent web tests still seed/assert `session_closed` in `view.test.ts`. The issue task and literal-search acceptance criteria scoped the product deliverable to `packages/server/src/Mohist.Server/Sessions/`, so this is not a candidate failure; it is a remaining product-level legacy alias if the cleanup is later expanded to web projections.
  SuggestedAction: Decide in a separate web-facing cleanup whether the web session model should also drop the underscore alias and update its tests to seed only `session.closed`.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-3]
  Severity: info
  Scope: acceptance evidence
  Evidence: Usage/cost reporting and activity feed assembly are now independent services: `AgentUsageReporter` is a sealed `IScopedService` at `packages/server/src/Mohist.Server/Sessions/Services/AgentUsageReporter.cs:33`, `AgentActivityFeedAssembler` is a sealed `IScopedService` at `packages/server/src/Mohist.Server/Sessions/Services/AgentActivityFeedAssembler.cs:41`, and `/activity`, `/usage`, and `/cost` routes resolve those services at `packages/server/src/Mohist.Server/Api/AgentRoutes.cs:33`, `:41`, and `:47`. `AgentSessionQuerier` no longer contains `GetActivityAsync`, `GetUsageTimeseriesAsync`, `GetCostRollupAsync`, `GetCostWindowedAsync`, `ToActivityCard`, `BuildTaskProgressMapAsync`, `ToAgentSessionDto`, or `record AgentSessionDto`; the targeted `git grep` returned only XML references to the new services.
  SuggestedAction: None.
  Status: out-of-scope

- [ID: item-4]
  Severity: info
  Scope: acceptance evidence
  Evidence: Transcript loading is centralized in `TranscriptPartLoader.LoadAsync` at `packages/server/src/Mohist.Server/Sessions/Services/TranscriptPartLoader.cs:16`, with former call sites delegating through it from `AgentSessionQuerier.cs:507`, `:566`, `:665`, `:829`, `:846`, and `AgentActivityFeedAssembler.cs:257`. The shared projection helper restores sequence/id ordering at `AgentSessionQuerier.cs:871`. Context-reference construction is centralized in `AgentSessionContextRefs.TryBuild` at `packages/server/src/Mohist.Server/Sessions/Services/AgentSessionContextRefs.cs:18`, with both DTO builders delegating at `AgentSessionQuerier.cs:296` and `:553`.
  SuggestedAction: None.
  Status: out-of-scope

- [ID: item-5]
  Severity: info
  Scope: acceptance evidence
  Evidence: Transcript closure event vocabulary is unified in server code: both `RuntimeEventTypes.SessionClosed` and `TranscriptPartTypes.SessionClosed` equal `session.closed` at `packages/server/src/Mohist.Server/Sessions/Services/TranscriptEventTypes.cs:7` and `:29`; `TranscriptAccumulator` maps runtime close events to the same transcript part constant at `TranscriptAccumulator.cs:216`; a targeted grep for `session_closed` under server session source/tests returned no matches.
  SuggestedAction: None.
  Status: out-of-scope

- [ID: item-6]
  Severity: info
  Scope: verification
  Evidence: Session-scoped server verification passed after the current candidate snapshot and the doc-reference repair: `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter "FullyQualifiedName~Sessions"` reported 295 passed, 2 skipped. Focused regression coverage now includes `TranscriptPartLoaderSpecs`, `AgentSessionContextRefsSpecs`, `AgentActivityFeedAssemblerSpecs`, `AgentUsageReporterSpecs`, sequence-order regressions in `GenericAgentSessionSummarySpecs:109` and `AgentSessionSpecs:178`, and DI registration rows for the two new services in `MigratedServicesRegistrationSpecs:87-88`.
  SuggestedAction: None.
  Status: out-of-scope

- [ID: item-7]
  Severity: warning
  Scope: verification
  Evidence: A full `npm test` rerun after the small doc-reference repair failed in unrelated `Mohist.Server.Tests.Specs.SystemSpecs.SystemUpdateServiceSpecs.RunUpdateAsync_OnBuildFailure_RunnerRestoreFails_MarksFailedWithUnavailableCapability`: expected status `Failed`, actual status `Restoring runner` at `SystemUpdateServiceSpecs.cs:962`. This test is outside the Session candidate surface. The same full suite completed successfully immediately before the doc-only repair, the post-repair Session-scoped server suite passed as item-6 describes, and an isolated rerun of the failing SystemUpdateService spec passed: 1 passed, 0 failed.
  SuggestedAction: Re-run or investigate the unrelated SystemUpdateService spec before integrate if the pipeline requires a fully green monorepo test run.
  Status: out-of-scope

<promise>PASS</promise>
