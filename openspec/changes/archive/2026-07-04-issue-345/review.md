# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs, packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs, packages/server/src/Mohist.Server/Api/AgentSessionFollowupRoutes.cs
  Evidence: The new server-authoritative success close satisfies the terminal-state criterion, but it breaks the follow-up criterion. `AgentJobGrain.ReportResultAsync` now appends `session.closed` with `status=completed` for successful generic jobs at `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:174-177`. The generic follow-up resolver treats any persisted terminal close as inactive because `ResolveGenericFollowupTargetAsync` sets `IsActive=false` when `ReadTerminalStateAsync(...) is not null` at `packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs:365-368`, and the follow-up route returns 409 for inactive sessions at `packages/server/src/Mohist.Server/Api/AgentSessionFollowupRoutes.cs:78-80`. Existing specs intentionally enforce that terminal generic sessions reject follow-ups at `packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionFollowupApiSpecs.cs:250-270`. Therefore, after the initial successful agent run completes, the product `/agent-sessions/{sessionId}/followup` API rejects the same-session follow-up that issue #345 explicitly requires to produce another visible turn. The new transcript follow-up test bypasses the product follow-up API and posts directly to the runner runtime-events endpoint at `packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs:276-327`, so it does not catch this regression. [disallowed:product-behavior-change]
  SuggestedAction: Reconcile terminal session status with reusable generic sessions. The product follow-up path must allow the intended completed-but-reusable state, or the design must introduce a separate per-job terminal fact that does not make the session inactive for follow-up delivery. Add a product-level regression that completes the first generic job, posts `/agent-sessions/{sessionId}/followup`, then verifies the follow-up turn is executed and visible in the transcript API.
  Verification: Code inspection. Targeted server specs passed (`dotnet test Mohist.sln -p:SkipWebBuild=true --filter "FullyQualifiedName~AgentSessionLaunchRoutesSpecs|FullyQualifiedName~GenericAgentSessionTranscriptAxisSpecs|FullyQualifiedName~GenericAgentSessionFollowupApiSpecs"`, 34 passed), but none exercise follow-up after `ReportResultAsync` writes the completed close.
  Status: open

- [ID: item-2]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs, packages/runner/src/actions/acp-agent.ts, packages/server/src/Mohist.Server/Sessions/Services/TranscriptEventSummaryProjector.cs
  Evidence: The failure path is no longer unchanged. For generic agent-job failures, the runner already emits `session.closed` with its `failureCategory` at `packages/runner/src/actions/acp-agent.ts:47-48` because `!ok` makes the guard true. The candidate then appends a second failed close from `AgentJobGrain.ReportResultAsync` at `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:176-177`, using `failureCategory = result.Status` rather than the runner's more specific category. Session event summaries keep the latest non-null failure category while iterating session-closed parts at `packages/server/src/Mohist.Server/Sessions/Services/TranscriptEventSummaryProjector.cs:21-24`, and terminal facts also use latest-close ordering at `packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs:846-855`. A reported prompt timeout, probe failure, or provider-specific category can therefore be overwritten by a generic `failed` category from the server-added close. [disallowed:product-behavior-change]
  SuggestedAction: Keep the server close for successful reports, but do not append a second server close for normal runner-reported failures unless the server can preserve the runner's specific failure metadata. Leave `FailWithReasonAsync` as the server-side path for dispatch/report-timeout failures that never produced a runner close, or add explicit dedup/merge behavior with tests.
  Verification: Code inspection; targeted server specs passed but do not cover runner-close plus report-close metadata precedence.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs, packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionFollowupApiSpecs.cs
  Evidence: There is no regression coverage for the product follow-up path after a successful completed generic job, which is the highest-risk interaction created by this change. `GenericLaunch_FollowUpRuntimeEvents_AppendNonEmptyTranscriptContent` verifies that manually posted runtime events can append content after completion, but it never calls `/api/projects/{project}/agent-sessions/{sessionId}/followup`, never exercises `ResolveGenericFollowupTargetAsync`, and never sends the SignalR `ReceiveFollowup` message. Existing follow-up route tests cover active pre-terminal sessions and terminal-session conflict behavior, but not the new issue #345 flow where completed job state and follow-up reuse must both be true.
  SuggestedAction: Add a server integration spec that launches a generic session, polls/reports the initial job as completed, registers the runner connection, sends a follow-up through the product endpoint, and verifies the runner receives the generic follow-up target. Pair it with transcript assertions after the follow-up runtime events are recorded.
  Verification: Code inspection; targeted server specs passed, confirming the current test suite does not catch item-1.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: issue acceptance criterion: real opencode execution
  Evidence: The issue requires an end-to-end check where the runner really executes opencode and the transcript turn's `messages` and `events` are non-empty and consistent with the real conversation. The candidate provides fake-agent and direct runtime-events coverage, which is appropriate for automated regression tests, but no evidence file, test output, or progress entry shows a real opencode generic session was launched and verified through the session detail/transcript API. `openspec/changes/issue-345/progress.txt:25-27` explicitly states the transcript pipeline was confirmed by tests and notes only server/runner test commands, not a live opencode run. [disallowed:external/manual-verification]
  SuggestedAction: Before integration, run one real generic AgentSession through the installed opencode agent and record the session id plus transcript API evidence showing non-empty assistant messages, tool events, usage, and terminal status. Keep this outside automated tests if live model/network access is not suitable for CI.
  Verification: Reviewed issue AC, progress artifact, and changed tests. No live-run evidence found.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: openspec/changes/issue-345/tasks.json
  Evidence: `tasks.json` still marks all implementation tasks with `passes: false` while `progress.txt` and the changed code/tests show T-001 through T-003 were implemented. This does not affect product behavior and is not the deliverable boundary, but it is stale traceability evidence during integration.
  SuggestedAction: Update task status metadata if the workflow consumes it for merge readiness or audit trails.
  Status: follow-up

## Pre-existing or Out-of-scope Items

None.

<promise>FAIL</promise>
