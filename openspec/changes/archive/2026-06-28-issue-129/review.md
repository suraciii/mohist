# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs
  Evidence: The generic read path correctly requires `source-kind = agent-launch` in `FindGenericSessionAsync` before returning a session (lines 366-375), but `ResolveGenericFollowupTargetAsync` and `ResolveGenericCancelTargetAsync` only verify the project label before returning a target (lines 161-186 and 209-226). A workflow session in the same project can therefore be addressed through `POST /agent-sessions/{workflowSessionId}/followup` or `/cancel`, even though those endpoints are specified as generic AgentSession surfaces. Followup would send a `target.kind = generic` SignalR message for a workflow session and may report `sent` while the runner drops it; cancel may report generic cancel state for a workflow-owned session. [disallowed:product-behavior-change]
  SuggestedAction: Reuse the same generic-session guard as `FindGenericSessionAsync` in both generic followup and cancel resolvers, and add integration tests that a workflow session id returns 404 on the generic followup/cancel endpoints.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter "FullyQualifiedName~GenericAgentSessionFollowupApiSpecs|FullyQualifiedName~GenericAgentSessionCancelApiSpecs"` after adding the regression cases.
  Status: open

- [ID: item-2]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Workflow/Services/Sessions/AgentSessionQuerier.cs; packages/server/src/Mohist.Server/Sessions/Grains/AgentSessionGrain.cs
  Evidence: Terminal detection for generic followup/cancel reads `session.closed` from persisted transcript rows (`ReadTerminalStateAsync`, lines 237-267), but `AgentSessionGrain.AppendRuntimeEventsAsync` only updates in-memory state and arms a persistence timer before returning (lines 359-365); the actual transcript save happens later in `PersistCallback`/`FlushAsync` (lines 564-627). For `session.closed`, the domain transition only records activity (`ApplyRuntimeEventToDomain`, lines 762-772), so `StatusName` remains `active` for the five-minute activity window (`AgentSessionJsonHelper.cs` lines 11-16) until the DB transcript is flushed. The tests explicitly drain/deactivate before asserting terminal behavior (`GenericAgentSessionFollowupApiSpecs.cs` lines 258-274, `GenericAgentSessionCancelApiSpecs.cs` lines 125-144), so they miss the immediate post-close window where followup can be accepted and cancel can call the runner or return `not-cancellable` instead of the terminal state. [disallowed:product-behavior-change]
  SuggestedAction: Make terminal state part of the session state or otherwise resolve it from the grain/current state synchronously, not only from eventually persisted transcript rows. Add tests that append `session.closed` and immediately call followup/cancel without deactivation or delay.
  Verification: Add immediate-after-close tests expecting followup 409, cancel `{ state: "completed" }`, and no runner invocation; then run the server test filter above.
  Status: open

- [ID: item-3]
  Severity: warning
  Scope: packages/server/src/Mohist.Server/Api/AgentSessionLaunchRoutes.cs; packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs
  Evidence: Launch accepts optional context refs (`issueNumber`, `epicNumber`, `repository`, `workspacePath`) and records them as metadata in `BuildContext`/`GenericAgentSessionMetadata` (launch route lines 134-150; metadata lines 70-88), but the `AgentJobInput` execution snapshot only carries the trimmed prompt, workspace path, agent id/instructions/config, and session id (launch route lines 80-89). `ComposePromptWithEntry` then sends only `{ instructions, config, prompt }` to the external agent (AgentJobGrain lines 365-373). The issue and specs require optional context references to be metadata / prompt context, so repository/issue/epic context is not actually delivered to the external agent. [disallowed:product-behavior-change]
  SuggestedAction: Define the prompt-context shape and include supplied context refs in the composed launch input sent to the runner, while keeping them metadata-only with respect to scope/mount/supervisor lifecycle. Add a launch/composition test asserting context refs are present in the dispatched prompt.
  Verification: Run `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentSessionLaunchRoutesSpecs|FullyQualifiedName~AgentJobGrainSpecs"`.
  Status: open

- [ID: item-4]
  Severity: warning
  Scope: packages/cli/Mohist.Cli/BodyInputResolver.cs; packages/cli/Mohist.Cli/MohistCliCommands.Issue.cs
  Evidence: `BodyInputResolver.ResolveAsync` now rejects any resolved whitespace-only body (lines 96-102). That is correct for the new `--prompt` and `--text` surfaces, but the same helper is used by existing issue body commands, including `issue update` (MohistCliCommands.Issue.cs lines 453-469). Previously `issue update --body-file empty.md` could send `"body": ""` to clear an issue body because body presence is tracked separately; now the CLI exits before sending the PATCH. This is a regression in a changed shared helper. [disallowed:product-behavior-change]
  SuggestedAction: Add an option/flag to `BodyInputResolver` so prompt/text callers can require non-empty content without changing issue body/comment/feedback behavior. Add regression coverage for clearing an issue body via empty file/stdin.
  Verification: Run `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore --filter FullyQualifiedName~CliIssueUpdatePatchBodySpecs`.
  Status: open

## Follow-up Items

- [ID: item-5]
  Severity: follow-up
  Scope: packages/runner/src/server/runner-signalr.ts; packages/runner/src/runtime/host.ts
  Evidence: The legacy `ReceiveFollowup` fallback creates a workflow `SessionTarget` with `projectId: ""` when the payload has only top-level `workflowRunId/sessionName` (runner-signalr.ts lines 845-848). `RunnerHost.resolveFollowupTarget` rejects that target when the runner has a configured `projectId` (host.ts lines 108-116). Current server code sends the new `target` shape for issue-scoped followup, so this is not the primary same-version path, but the fallback comment says older server payloads keep working and the tests cover it only with a fake resolver.
  SuggestedAction: Either remove/clarify the unsupported fallback or have the host resolve the configured project id for legacy workflow payloads. Add a host-level test for a legacy workflow payload against a configured runner project.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-6]
  Severity: info
  Scope: dependency audit
  Evidence: The server test build output includes `npm audit` reporting 9 vulnerabilities (3 moderate, 3 high, 3 critical). This appears unrelated to the issue-129 product change and did not cause test failure.
  SuggestedAction: Track separately through dependency maintenance.
  Status: out-of-scope

## Verification

- `npm run typecheck -w packages/runner` passed.
- `npm test -w packages/runner -- --run tests/runner-signalr.spec.ts tests/runner-host.spec.ts tests/acp/session-strategies-generic.spec.ts tests/acp/session-target.spec.ts` passed: 85 tests.
- `npm test -w packages/runner` passed: 722 passed, 23 skipped.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore --filter FullyQualifiedName~CliAgentSessionCommandSpecs` passed: 36 tests.
- `dotnet test packages/cli/tests/Mohist.Cli.Tests/Mohist.Cli.Tests.csproj --no-restore` passed: 428 tests.
- `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentSessionLaunchRoutesSpecs|FullyQualifiedName~GenericAgentSessionFollowupApiSpecs|FullyQualifiedName~GenericAgentSessionCancelApiSpecs|FullyQualifiedName~AgentJobGrainSpecs"` passed: 59 tests.
- `npm test` passed in this workspace run; output showed the runner suite completing with 722 passed and 23 skipped. It also surfaced the out-of-scope npm audit warnings noted above.

<promise>FAIL</promise>
