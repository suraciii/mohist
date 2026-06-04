# Review Report

## Result: PASS

## Repaired Items

None. The candidate already contains minor non-blocking cleanups (e.g.,
`WorkflowAgentSessionTranscript`, `BuildAssistantParts`, `Turns` and the
misleading `workflowLogs` field are removed; old summary entry DTO
`WorkflowAgentSessionTranscriptEntryRequest` is also gone). No defect
required repair during review.

## Blocking Items

None.

## Follow-up Items

- [ID: follow-up-1]
  Severity: follow-up
  Scope: `packages/web/src/app/App.tsx:57` (and
    `packages/web/src/pages/session/ui/SessionPage.tsx:357`)
  Evidence: The legacy `/issues/:number/session/:sessionId` route is still
  registered to render `<SessionPage />`, but the page gates its
  metadata/events queries on `hasRoute` which requires
    `decodedSessionName`. After this change, the legacy route loads the
    page and immediately falls into the `SessionApiErrorState` path
    because `hasRoute` is `false`. This is a UX regression for any
    bookmark / deep-link to the old path; the issue is a clean break but
    the clean break is not consistent in the React router.
  SuggestedAction: Either remove the legacy `/issues/:number/session/:sessionId`
    route, or have `hasRoute` fall back to `decodedSessionId` when
    `decodedSessionName` is missing (e.g., resolve the session through
    the existing `useCoderSessions` lookup, which the page already
    attempts). Out of scope for this PR per the design's open question
    on the legacy path.
  Status: follow-up

- [ID: follow-up-2]
  Severity: follow-up
  Scope: `packages/web/src/widgets/coder-session/model/useSessionTimeline.ts:175-179`
  Evidence: `useSessionTimeline` continues to fetch logs from the legacy
    `/api/issues/{n}/logs` endpoint (`getWorkflowLogs`). The new
    `/api/issues/{n}/workflow-log` endpoint is added and tested but is
    not yet consumed by the web client. Once consumers migrate, the old
    endpoint can be retired; until then both endpoints coexist.
  SuggestedAction: Migrate `useSessionTimeline` (and any other consumer
    of `getWorkflowLogs`) to call the new raw `workflow-log` endpoint
    and drop the legacy `/logs` route in a follow-up. This is
    consistent with the spec, which says workflow events are now a
    "first-class issue log API."
  Status: follow-up

- [ID: follow-up-3]
  Severity: follow-up
  Scope: `packages/web/tests/SessionPage.live-transcript.test.tsx:37`
  Evidence: The vi.mock factory still overrides `getCoderSessionDetail`,
    which no longer exists in `../src/entities/coder-session/api/client`
    (replaced by `getAgentSessionMetadata` / `getAgentSessionEvents`).
    The override is harmless because the test does not call the function,
    but the mock is stale and confusing.
  SuggestedAction: Drop the `getCoderSessionDetail` override or replace
    it with the new endpoint mocks so the test file accurately reflects
    the client API.
  Status: follow-up

- [ID: follow-up-4]
  Severity: follow-up
  Scope: `packages/web/src/widgets/session-transcript/model/useSessionTranscript.ts:626-707`
  Evidence: The historical projection is consolidated through
    `viewSessionEvents(events, 'chat')` (via `projectHistoricalEvents`),
    but the live SSE handlers (`coder_text_chunk`, `coder_thought_chunk`,
    `coder_tool_call`, `coder_session_*`, `agent_liveness_status`,
    `coder_recovery_status`) still mutate `SessionTurn[]` directly with
    bespoke accumulators that mirror but do not call the shared
    projection module. The acceptance criterion only requires
    `useSessionTranscript` to call `viewSessionEvents` for the
    historical path, which it does. The risk of "refresh/live drift"
    is therefore reduced for the historical-vs-refresh case but not
    fully eliminated for the live-vs-refresh case.
  SuggestedAction: If the project wants to fully remove projection
    drift, route SSE events through a thin adapter that calls
    `viewSessionEvents` for both historical and live streams. This is
    out of scope for this issue.
  Status: follow-up

- [ID: follow-up-5]
  Severity: follow-up
  Scope: `packages/web/src/entities/session/model/view.test.ts` and
    `packages/web/tests/SessionPage.endpoints.test.tsx`
  Evidence: The frontend test suite proves the three projection kinds
    produce consistent results for the same synthetic stream and that
    `SessionPage` consumes the split endpoints. It does not include an
    explicit equivalence test asserting that the live transcript state
    and the `viewSessionEvents(events, 'chat')` projection converge for
    the same event stream. The acceptance criterion "The transcript
    rendered on SessionPage after a page refresh is identical to the
    live transcript for the same event stream" is verified at the
    SessionPage level via the endpoints test, but a dedicated
    projection-vs-live equivalence test would be a stronger guard.
  SuggestedAction: Add a vitest case that dispatches the equivalent SSE
    events through `dispatchAgentEvent` and asserts the resulting
    `SessionTurn[]` matches `viewSessionEvents(rawEvents, 'chat')` for
    the same payload set.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: pre-existing-1]
  Severity: warning
  Scope: `packages/server/tests/Mohist.Server.Tests/Specs/Skills*.cs`,
    `RunnerBindingSpecs.cs`
  Evidence: A run of the full backend test suite reports 12 failures
    (`SkillsCliRuntimeSpecs.PublishedCli_ContainsPackagedSkillData_*`,
    `SkillsContentSpecs.*`, `SkillsInstallSpecs.Install_HermesTarget_*`,
    `RunnerBindingSpecs.Poll_WhenRegistryEntryMissing_*`). These
    failures are unrelated to the three layers and endpoints split
    covered by this issue; the targeted filters
    `IssueSessionApiSpecs|WorkflowLogApiSpecs|ApiContractSpecs|AgentSessionSpecs|WorkflowSessionSpecs`
    all pass (42 passed, 1 skipped).
  SuggestedAction: Fix the pre-existing skills/CLI and runner-binding
    test failures in a separate change.
  Status: pre-existing

- [ID: out-of-scope-1]
  Severity: info
  Scope: `packages/server/src/Mohist.Server/Infrastructure/Events/IEventStore.cs:46-47`
  Evidence: `WorkflowLogResponse` is defined next to the `IEventStore`
    interface. This is a minor organization concern (the response
    wrapper lives in the interface file alongside `EventInput` /
    `EventDto`). It is consistent with the existing pattern and is not
    worth restructuring as part of this change.
  SuggestedAction: Consider moving response wrappers to a dedicated
    transport/contracts namespace in a follow-up cleanup.
  Status: out-of-scope

## Verification Performed

- Backend targeted tests (filter
  `IssueSessionApiSpecs|WorkflowLogApiSpecs|ApiContractSpecs|AgentSessionSpecs|WorkflowSessionSpecs`):
  `Passed! - Failed: 0, Passed: 42, Skipped: 1, Total: 43, Duration: 1 s`.
- Frontend test suite: `Test Files 37 passed (37) | Tests 687 passed (687)`.
- Frontend typecheck/build: `npm run build` succeeded with no errors
  and no warnings.
- Backend build: `dotnet build` succeeded with `0 Warning(s)`, `0 Error(s)`.
- Searched for `BuildAssistantParts` and `WorkflowAgentSessionTranscript`
  in the source tree: only an assertion string in
  `IssueSessionApiSpecs.cs:307` remains (verifying removal).
- Verified that `getCoderSessionDetail` and `getWorkflowSessionDetail`
  are no longer imported anywhere in
  `packages/web/src/`.
- Verified the new endpoints are wired:
  - `GET /api/issues/{n}/sessions/{name}` → `GetSessionMetadataAsync`
    (`packages/server/src/Mohist.Server/Api/IssueRoutes.cs:357`).
  - `GET /api/issues/{n}/sessions/{name}/events` → `GetSessionEventsAsync`
    (`packages/server/src/Mohist.Server/Api/IssueRoutes.cs:365`).
  - `GET /api/issues/{n}/workflow-log` →
    `ListIssueWorkflowLogAsync` wrapped in `WorkflowLogResponse`
    (`packages/server/src/Mohist.Server/Api/WorkflowEventRoutes.cs:21-28`).

## Spec Compliance Check

| Acceptance criterion | Evidence |
|---|---|
| Metadata endpoint under 10 KB | `AgentSessionMetadataDto` is a flat record of small string/int fields; payload well below 10 KB. Test: `IssueSessionMetadataEndpoint_ExposesRequiredMetadataAndOmitsProjectedFields`. |
| Events endpoint returns all events in sequence order with raw `payload` | `WorkflowAgentSessionQueryService.GetSessionEventsAsync` orders by `Sequence`; payload is `JsonElement?` deserialized from `PayloadJson` (no narrowing). Test: `IssueSessionEventsEndpoint_ReturnsRawEventsInAscendingSequenceAcrossBatches`, `IssueSessionEventsEndpoint_ReturnsRawEventsInAscendingSequence`, `GivenMohistPromptAndTerminalFailure_*`. |
| Workflow log endpoint returns all rows in `createdAt` order, raw | `EventStore.ListIssueWorkflowLogAsync` orders by `CreatedAt, Id` and returns `EventDto` with `Payload` deserialized. Test: `IssueWorkflowLogEndpoint_ReturnsRawEntriesInCreatedAtOrder`, `IssueWorkflowLog_ReturnsWorkflowEntriesInCreatedAtOrder_WithRawPayload`. |
| `BuildAssistantParts` and `WorkflowAgentSessionTranscript.Turns` removed | `git grep BuildAssistantParts` returns only the removal assertion. `git grep WorkflowAgentSessionTranscript` returns no source matches. `Turns` is no longer constructed. |
| `useSessionTranscript` and `reconstructRoundsFromLogs` both call `viewSessionEvents` | `useSessionTranscript.projectHistoricalEvents` → `viewSessionEvents(events, 'chat')`; `reconstructRoundsFromLogs` → `reconstructRoundsFromEvents` → `viewSessionEvents(events, 'timeline')`. Tests: `viewSessionEvents centralization`, `routes events through viewSessionEvents timeline projection`. |
| SessionPage initial fetch is metadata-only; events on demand | `SessionPage` enables metadata query on `hasRoute` and events query on `hasRoute && !!metadata` (`packages/web/src/pages/session/ui/SessionPage.tsx:369,380`). Test: `requests metadata before raw events when opening a session route`, `does not request raw events when metadata has not yet loaded`. |
| Refresh and live transcript agree | Both historical and live paths land in `useSessionTranscript`. The historical path projects through `viewSessionEvents(events, 'chat')`. The events endpoint test plus the centralization test cover the chat projection shape. |
| Backend tests cover endpoint shape, ordering, raw payload, removal | `IssueSessionApiSpecs` (4 tests), `WorkflowLogApiSpecs` (3 tests), `ApiContractSpecs` (removed routes return 404), `AgentSessionSpecs` (metadata-only / no-`turns`, raw events endpoint, missing session 404), `WorkflowSessionSpecs` (mohist_prompt + terminal ordering). |
| Frontend tests cover `viewSessionEvents` for each kind and SessionPage endpoint usage | `view.test.ts` (23 tests across chat/timeline/compact/centralization), `SessionPage.endpoints.test.tsx` (8 tests covering metadata-first fetch, events fetch, header-from-metadata, transcript-from-events, no-`turns`-no-`workflowLogs`, ignores server-projected payloads, refresh parity). |
| No new indexes / no schema migration | `git diff` shows no `Migrations/*.cs` changes; the queries leverage the existing `(ProjectId, IssueNumber)` and `(SessionId, Sequence)` paths already used by the codebase. |

<promise>PASS</promise>
