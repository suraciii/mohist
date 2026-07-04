# Review Report

## Result: FAIL

## Repaired Items

None.

## Blocking Items

- [ID: item-1]
  Severity: blocking
  Scope: packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs, packages/runner/src/actions/acp-agent.ts
  Evidence: The required success terminal-state fix is not implemented. The issue acceptance criteria require a generic session to enter `completed` after successful agent work and explicitly call out the success path that does not emit `session.closed`. The candidate still has the runner suppressing `session.closed` for successful generic agent jobs at `packages/runner/src/actions/acp-agent.ts:47-49`, while `AgentJobGrain.ReportResultAsync` only updates `_status` / `_terminalResult` and returns at `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:148-174`. The only server-side generic close writer remains `CloseGenericSessionOnFailureAsync`, which appends `session.closed` with `status=failed` at `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:456-475` and is called only from `FailWithReasonAsync` at `packages/server/src/Mohist.Server/Agent/Grains/AgentJobGrain.cs:500-512`. Since list/detail status resolves terminal state from persisted `session.closed` parts and otherwise returns `running` when an AgentSessionId is bound (`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs:276-284`, `:424-449`), a successful generic job can still remain `running`. [disallowed:product-behavior-change]
  SuggestedAction: Add the server-authoritative success close in `ReportResultAsync` for successful generic jobs with non-empty `_input.AgentSessionId`, using a `session.closed` payload with `status=completed`, and verify the session list/detail status resolves to `completed` after the job report.
  Verification: Code inspection above; targeted tests run successfully but do not exercise this missing path: `npm run typecheck -w packages/runner`; `npm test -w packages/runner -- session-events-observable-drop session-strategies-transcript-axis`; `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~AgentSessionLaunchRoutesSpecs`; `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj -p:SkipWebBuild=true --filter FullyQualifiedName~GenericAgentSessionTranscriptAxisSpecs`.
  Status: open

- [ID: item-2]
  Severity: test-gap
  Scope: packages/server/tests, packages/runner/tests/acp
  Evidence: There is no regression test for the successful generic job terminal-state path required by `openspec/changes/issue-345/specs/generic-agent-session-terminal-state/spec.md:1-47`. The new and existing runner generic tests still assert the opposite runner behavior: successful generic turns do not emit `session.closed` (`packages/runner/tests/acp/session-strategies-transcript-axis.spec.ts:253-254`, `packages/runner/tests/acp/session-strategies-generic.spec.ts:238-260`). The server launch-route specs include a timeout/failure terminal-state test at `packages/server/tests/Mohist.Server.Tests/Specs/Agent/Api/AgentSessionLaunchRoutesSpecs.cs:366-414`, but no corresponding completed-job test that proves a successful generic `ReportResultAsync` records `session.closed/completed` and resolves the session out of `running`. [disallowed:product-behavior-change]
  SuggestedAction: Add a server grain or integration spec that launches a generic session, reports the agent job as completed, flushes transcript persistence, then asserts a persisted `session.closed` part with `status=completed` and list/detail status `completed`. Keep a runner test documenting that the ACP session may remain cached for follow-ups.
  Verification: Search/read evidence above; targeted server specs passed but do not cover this success case.
  Status: open

- [ID: item-3]
  Severity: test-gap
  Scope: packages/runner/tests/acp/session-strategies-transcript-axis.spec.ts, packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs
  Evidence: The candidate does not provide the requested end-to-end fake-agent harness that drives launch -> polled dispatch -> runner execution -> server runtime-events endpoint -> persisted transcript in one chain. The runner harness calls `acpAgentAction` with a hand-built context whose `agentSessionId` is hard-coded to `session-abc` (`packages/runner/tests/acp/session-strategies-transcript-axis.spec.ts:15-32`, `:216`), so it does not exercise server launch, poll response parsing, or `WorkExecutor` context wiring from a real `WorkDispatchResponse`. The server transcript spec separately polls a launch dispatch, drains/reports it, and then manually posts runtime events to `/runtime-events` (`packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs:105-122`, `:168-181`), so it does not prove the runner fake agent's emitted `message.delta`, `tool_call.*`, or `usage.updated` events actually reach and persist through the server endpoint. This leaves the original `messages:0/events:0 despite runner execution` failure mode only partially covered. [disallowed:architectural/test-harness-judgment]
  SuggestedAction: Add one integration-style fake-agent regression that uses the launched/polled envelope as the input to runner execution, lets the fake ACP agent emit assistant/tool/usage updates, and asserts the real transcript API returns the persisted non-empty turn for the same minted session id.
  Verification: Code inspection above; targeted runner and server tests passed independently but are not connected end-to-end.
  Status: open

- [ID: item-4]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs
  Evidence: The follow-up acceptance criterion requires the transcript API to return both the initial and follow-up turns (`openspec/changes/issue-345/specs/generic-agent-session-transcript/spec.md:35-50`). The server follow-up spec posts a second `session.input` and follow-up events, but then only asserts `turns.GetArrayLength() >= 1` and `partCount >= 2` (`packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs:301-321`). It scans all assistant parts across all turns, so the test would still pass if the follow-up content were merged into the first turn or if only one turn were returned. [disallowed:test-coverage]
  SuggestedAction: Assert at least two turns and verify the first turn contains the initial prompt/reply while a later turn contains `transcript-axis follow-up` and `follow-up reply` plus the follow-up tool event.
  Verification: Code inspection above.
  Status: open

- [ID: item-5]
  Severity: warning
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs
  Evidence: The new server transcript specs complete/drain the launched agent job before they post the synthetic transcript events (`DrainRemainingDispatchAsync` before `OpenGenericSessionAsync` / `/runtime-events` at `packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs:112-117` and `:234-238`). A real runner run emits transcript events before reporting the job result. Once item-1 is fixed, reporting completion first will create a persisted `session.closed/completed` part before the synthetic assistant/tool events; transcript reads order turns by sequence (`packages/server/src/Mohist.Server/Sessions/Services/AgentSessionQuerier.cs:827-838`), and the transcript store creates a new turn when later `session.input` arrives (`packages/server/src/Mohist.Server/Infrastructure/Data/Sessions/AgentSessionTranscriptStore.cs:49-61`). That can make assertions like `turns[0].user.text == "transcript-axis events"` (`packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs:180-181`) fragile or incorrect after the required success close exists. [disallowed:test-harness-behavior]
  SuggestedAction: Reorder the harness to emit runtime events before reporting job completion, or execute the polled dispatch through the fake runner path so the event/report ordering matches production. Avoid assertions that depend on the first turn when a terminal close-only turn may be present.
  Verification: Code inspection above.
  Status: open

- [ID: item-6]
  Severity: test-gap
  Scope: packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs
  Evidence: Usage persistence and summary surfacing are not asserted on the server side, despite the spec requiring usage to appear in the transcript and session summary (`openspec/changes/issue-345/specs/generic-agent-session-transcript/spec.md:21-26`). The server test posts a `usage.updated` event with token/cost/context fields at `packages/server/tests/Mohist.Server.Tests/Specs/Sessions/GenericAgentSessionTranscriptAxisSpecs.cs:151-164`, but the transcript assertions only check text/tool assistant parts at `:177-194`, and the summary assertions only check `sessionId` at `:196-201`. [disallowed:test-coverage]
  SuggestedAction: Assert a usage transcript part is persisted and that the session summary exposes the expected token/context/cost values after `usage.updated`.
  Verification: Code inspection above.
  Status: open

- [ID: item-7]
  Severity: minor
  Scope: packages/runner/src/actions/acp/session-events.ts
  Evidence: The observable-drop log now reports `unresolved generic session target` whenever either the target is missing or `context.serverConnection` is absent (`packages/runner/src/actions/acp/session-events.ts:74-83`). With a valid `agentSessionId` but no server connection, the target is resolved but the log still says the generic session target is unresolved; the new test enshrines this case at `packages/runner/tests/acp/session-events-observable-drop.spec.ts:197-216`. This is misleading observability for an adjacent drop mode and can send debugging toward the launch/dispatch contract when the real issue is runner connection wiring. [disallowed:behavior/observability-contract]
  SuggestedAction: Split the conditions: log the specified unresolved-target warning only when `sessionTargetFromContext` returns null, and use a distinct message for missing `serverConnection` if that drop should also be observable.
  Verification: Code inspection above; targeted runner tests passed with the current misleading behavior.
  Status: open

## Follow-up Items

None.

## Pre-existing or Out-of-scope Items

None.

<promise>FAIL</promise>
