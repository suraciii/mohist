# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/runner/src/actions/acp/session-strategies.ts; packages/server/src/Mohist.Server/Api/RunnerRoutes.cs; packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs
  Evidence: Generic sessions are minted by the launch endpoint before dispatch, so the runner's first `getAgentSession(projectId, sessionId)` call returns an existing session from `RunnerRoutes.cs:267-272`. `runAcpGenericAgentSession` then skips `openAgentSession` because it only calls open when `existing` is null (`session-strategies.ts:142-149`). The server only stamps the authoritative runner id during the generic open path (`RunnerRoutes.cs:275-300`, `AgentSessionGrain.cs:79-82`), and both followup and cancel resolve the runner from the persisted session row (`AgentSessionQuerier.cs:178-186`, `AgentSessionQuerier.cs:220-226`). In the real launched path the session can therefore execute and emit runtime events while retaining an empty `RunnerId`, causing followup to return 409 inactive and cancel to return `not-cancellable` instead of addressing the live runner. The tests mask this by manually calling `/open` in `LaunchAndOpenGenericSessionAsync` instead of exercising the runner's actual `getAgentSession`-then-skip-open flow. [disallowed:product-behavior-change]
  SuggestedAction: Ensure the runner always binds the generic session to the runner id before running or resuming the ACP session. One minimal direction is to make the generic strategy call `openAgentSession` for the minted session even when `getAgentSession` returns an existing record that has no ACP session id/runner binding, then add an integration/runner test that launches through the product endpoint, lets the runner strategy process the dispatch without a manual open call, and verifies followup/cancel resolve the runner.
  Verification: Static trace across `runAcpGenericAgentSession`, generic runner routes, and generic followup/cancel resolvers; existing tests show the gap because helper `LaunchAndOpenGenericSessionAsync` manually posts `/open` before followup/cancel assertions.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Agent/Api/AgentSessionLaunchRoutesSpecs.cs; packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs
  Evidence: The candidate's own acceptance coverage for "On AgentJob timeout the grain transitions the session to a terminal failed state" does not pass. Running `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentSessionLaunchRoutesSpecs|FullyQualifiedName~GenericAgentSessionFollowupApiSpecs|FullyQualifiedName~GenericAgentSessionCancelApiSpecs|FullyQualifiedName~AgentJobGrainSpecs"` failed `AgentSessionLaunchRoutesSpecs.Launch_AgentJobTimeout_TransitionsGenericSessionToTerminalFailedState`: `Agent job did not reach Failed within 30s (last status Running)`. That leaves the issue acceptance criterion and task T-003 timeout requirement unmet. [disallowed:product-behavior-change]
  SuggestedAction: Fix the AgentJob timeout path for launched generic sessions so running jobs hit `ReportTimeout`, close the associated generic session with a terminal failed event, and make the failing test deterministic enough to pass in the filtered server suite.
  Verification: Re-run the filtered server test command above and then the broader server suite.
  Status: open

- [ID: item-3]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Api; packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs
  Evidence: The launch response returns only `{ sessionId, agentId, agentName, status }` (`AgentSessionLaunchRoutes.cs:92-100`), and the only generic-session id GET route added is runner-internal under `/api/runner/{runnerId}/agent-sessions/{projectId}/{sessionId}` (`RunnerRoutes.cs:261-343`). Existing transcript and metadata product routes remain issue-scoped and require `{number}/sessions/{name}` (`IssueRoutes.Sessions.cs:24-46`), while `GetSessionTranscriptAsync` still resolves through issue/workflow labels (`AgentSessionQuerier.cs:343-374`). A generic session without workflow run/session name therefore has no product API transcript entry despite the issue requiring callers to receive a transcript entry and the specs requiring the session to be observable through existing read paths by session id. [disallowed:public-contract-change]
  SuggestedAction: Add or expose a product read path for generic AgentSession metadata/transcript by project and session id, or return an existing valid transcript URL from launch, and cover it with an API test that launches a generic session and reads its transcript without an issue number or workflowRunId.
  Verification: Add API tests for product-level generic session metadata/transcript access and verify launch output includes a usable transcript entry or documented route.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: packages/runner/tests/acp/session-strategies-generic.spec.ts; packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionFollowupApiSpecs.cs; packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionCancelApiSpecs.cs
  Evidence: The regression suite does not exercise the real end-to-end lifecycle for a minted generic session. Runner tests use a fake `getAgentSession` that always returns null (`session-strategies-generic.spec.ts:91-99`), so the generic strategy always enters the open path and never covers the production case where the launch endpoint already created the session. Server followup/cancel tests manually post `/api/runner/{runnerId}/agent-sessions/{projectId}/{sessionId}/open` in `LaunchAndOpenGenericSessionAsync` before testing product followup/cancel, bypassing the runner behavior under review. This coverage gap allowed item-1 to ship. [disallowed:test-design-change]
  SuggestedAction: Add a runner unit test where `getAgentSession` returns the pre-minted generic session without `acpSessionId`, and assert the strategy still binds/opens before running. Add a server or integration test that performs launch plus actual runner dispatch processing, then validates followup and cancel without a manual `/open` setup call.
  Verification: New tests should fail before the item-1 fix and pass after it.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs
  Evidence: `ReadTerminalStateAsync` and `IsTerminalSessionAsync` duplicate the same transcript query and JSON parsing logic for `session.closed`/`session_closed` status detection. This is not the source of the current failure, but it increases the chance of future divergence in terminal-state handling.
  SuggestedAction: After the blocking lifecycle issues are fixed, consolidate the terminal-state read into one helper that returns the terminal status and have followup/cancel share it.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: repository verification
  Evidence: `npm run typecheck -w packages/runner`, `npm test -w packages/runner`, and `npm run typecheck -w packages/web` passed. `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore --filter "FullyQualifiedName~CliAgentSessionCommandSpecs"` passed 36 tests. A broad `npm test -- --filter Mohist.Cli.Tests --no-restore` attempt timed out after 120 seconds while the .NET server suite was still running, so it is not counted as a candidate pass/fail beyond confirming the command exceeded the review timeout.
  SuggestedAction: Re-run the broad server/monorepo test command with a longer timeout after fixing the blockers.
  Status: out-of-scope

<promise>FAIL</promise>
