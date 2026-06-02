# Review Report

## Result: PASS

The change rebuilds the session transcript from the canonical
`workflow_agent_session_events` stream, populates `turn.user.text` from the
`mohist_prompt` payload, splits turns by `mohist_prompt` in event order,
preserves reasoning / text / tool interleaving, projects liveness / terminal /
recovery events as transcript parts, removes the `agent_session_terminal` /
`agent_liveness_status` filter from the latest-events query, and renders a
`legacy-missing` prompt for sessions without a `mohist_prompt` event. All nine
issue-47 acceptance criteria are covered by tests that pass locally; the six
pre-existing failures in `GitSourceInspectorSpecs`, `IssueApiSpecs`,
`PausingWorkSpecs`, and `StageLockSpecs` reproduce on the pre-change commit and
are unrelated to this work.

## Repaired Items

- [ID: item-1]
  Severity: info
  Scope: dead code
  Evidence: `BuildTurnParts` ended with an `if (parts.Count == 0 && session.CompletedAt is null) return parts; return parts;` block. Both branches return `parts`, so the conditional is a no-op carried over from the original `BuildAssistantParts`. The function reads more clearly as a single trailing `return parts;` after the loop.
  Verification: Removed the dead conditional in
  `packages/server/src/Mohist.Server/Sessions/Queries/WorkflowAgentSessionQueryService.cs:524-527`,
  re-ran `dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj`
  (Build succeeded. 0 Warning(s) 0 Error(s)) and
  `dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --filter "FullyQualifiedName~AgentSessionSpecs|FullyQualifiedName~WorkflowSessionSpecs"`
  (Passed! - Failed: 0, Passed: 28, Skipped: 1, Total: 29). The full web
  suite still passes (Test Files 34 passed, Tests 658 passed).
  Status: resolved

## Blocking Items

None.

## Follow-up Items

- [ID: item-2]
  Severity: follow-up
  Scope: transcript projection ordering
  Evidence: `BuildTurns` (`WorkflowAgentSessionQueryService.cs:200-233`) collects
  assistant events with `for (var k = startIdx + 1; k < nextPromptIdx; k++)`, so
  events whose index is strictly less than the first `mohist_prompt` index are
  silently dropped. The runner's normal order (`emitSessionStarted` then
  `emitSessionEvent("mohist_prompt", ...)` in `acp-agent.ts:423-424`) keeps
  `mohist_prompt` as the first event, so this is a theoretical edge rather than
  a known failure. The self-review (`item-3`) tracks the same concern.
  SuggestedAction: When a `coder_recovery_status`, liveness, or terminal event
  arrives before the first `mohist_prompt`, decide whether to (a) drop it, (b)
  attach it to a synthetic pre-prompt slot, or (c) fold it into the first
  opened turn's part list retroactively. Document the runner invariant in
  `acp-agent.ts` if events are guaranteed to be ordered.
  Status: follow-up

- [ID: item-3]
  Severity: follow-up
  Scope: tool_call_update before tool_call
  Evidence: `TurnToolIndex.TryGet` returns false when an update arrives for a
  `toolCallId` that has no preceding `tool_call` in the same turn. The merge
  branch is then skipped, and the orphaned update event is dropped. The
  self-review (`item-4`) flagged the same gap.
  SuggestedAction: When an update arrives for an unknown `toolCallId`, decide
  whether to (a) materialize a tool part from the update payload, (b) drop the
  orphaned update, or (c) treat it as a delayed create and append a new part.
  Add a small spec assertion for the chosen rule.
  Status: follow-up

- [ID: item-4]
  Severity: follow-up
  Scope: liveness part shape
  Evidence: The proposal allowed `agent_liveness_status` parts to be a divider
  or error part. The implementation picks `ErrorPart { kind: "recovery", ... }`
  to match the live `useSessionTranscript` handler. The web bundle still ships
  a "Show full prompt" disclosure that reads `turn.user.text`; the live
  `useSessionTranscript.onAgentEvent('agent_liveness_status', ...)` produces a
  similar `recovery` error card. If the live page ever introduces a distinct
  divider shape, the backend will need to mirror it.
  SuggestedAction: Re-read `packages/web/src/widgets/session-transcript/model/useSessionTranscript.ts:876`
  and the matching `useSessionTimeline.ts:643` handlers before any future
  shape change; if a divider part is added to the live path, mirror it in
  `BuildLivenessErrorPart` so live and replay render identically.
  Status: follow-up

- [ID: item-5]
  Severity: follow-up
  Scope: `applyReasoningReorder` post-pass
  Evidence: With the backend now authoritative for ordering, the
  `applyReasoningReorder` post-pass in `packages/web/src/widgets/session-transcript/model/session-transcript-display.ts`
  is defense in depth. The self-review (`item-6`) deferred its removal.
  SuggestedAction: After this change ships, sample prod transcripts to confirm
  the post-pass is a no-op on backend-projected turns, then remove it in a
  follow-up to simplify the rendering pipeline. Do not remove it in this
  change.
  Status: follow-up

- [ID: item-6]
  Severity: follow-up
  Scope: `coder_recovery_status` runner coverage
  Evidence: The backend now projects `coder_recovery_status` events
  (`BuildRecoveryErrorPart`, `WorkflowAgentSessionQueryService.cs:515-521`) and
  the spec test `RunnerReportsCoderRecoveryTransitions_ProjectedAsRecoveryPartsWithLiveMapping`
  exercises the projection, but `packages/runner/src/actions/acp-agent.ts` does
  not currently emit `coder_recovery_status` to the session event stream. The
  projection is forward-compatible but not yet fed by any production code path.
  SuggestedAction: When the runner starts emitting `coder_recovery_status` to
  `/api/runner/.../events`, add a small test that exercises the projection
  through the runner path end-to-end. Until then, the backend projection
  remains dormant.
  Status: follow-up

## Pre-existing or Out-of-scope Items

- [ID: item-7]
  Severity: warning
  Scope: pre-existing
  Evidence: Six pre-existing tests fail on both `HEAD~7` (pre-change) and
  `mo/issue-47` (post-change): `GitSourceInspectorSpecs.Inspect_AfterNewCommit_SourceHeadDiffersFromCapturedHash`,
  `Inspect_DirtyRepo_ReturnsDirtyTrue`, `Inspect_CleanRepo_ReturnsPathBranchHeadAndNotDirty`,
  `IssueApiSpecs.SystemUpdateStatus_WhenNoJobExists_ReturnsIdleEnvelope`,
  `PausingWorkSpecs.PausedWorkflow_Resume_ContinuesWithNextTask`, and
  `StageLockSpecs.SameProjectIntegrateStages_RunSequentiallyAcrossWorkflows`.
  Reproduced on the pre-issue-47 commit; not introduced by this change.
  SuggestedAction: Out of scope for issue-47. File a separate issue to address
  the underlying git harness, system-update state, pause semantics, or stage
  lock behavior as appropriate.
  Status: pre-existing

## Spec & Acceptance Coverage

| Acceptance Criterion | Test / Evidence |
| --- | --- |
| `turns[0].user.text` equals `mohist_prompt.data.text` for `GET /api/issues/{n}/workflow/sessions/{name}` | `AgentSessionSpecs.MohistPrompt_RecordsFullPayload_UserTextEqualsEventText` (lines 263-322) and `WorkflowSessionSpecs.GivenMohistPromptAndTerminalFailure_WhenIssueWorkflowSessionIsQueried_ThenTurnsReflectsEventStreamAndTurnCount` (lines 80-146) assert `user.text == promptBody` and `user.text == longPromptBody` respectively. |
| `/issues/8/workflow/sessions/T-005.1` no longer returns short task title as `turn.user.text` | `AgentSessionSpecs.MohistPrompt_ShortSessionTitle_NotSubstitutedForRealPromptText` (lines 324-381) and `WorkflowSessionSpecs.GivenLegacySessionWithoutMohistPrompt_WhenIssueWorkflowSessionIsQueried_ThenTranscriptReturnsLegacyMissingTurn` (lines 148-210) assert `user.text != shortSessionTitle` and `user.text != sessionId` etc. |
| Two `mohist_prompt` events produce two turns in event order | `AgentSessionSpecs.MohistPrompt_TwoEventsInOneSession_ProduceTwoTurnsInEventOrder` (lines 383-451) and `WorkflowSessionSpecs.GivenTwoMohistPromptEvents_WhenIssueWorkflowSessionIsQueried_ThenTranscriptProducesTwoTurnsInEventOrder` (lines 212-280) assert `turns.Length == 2`, ordered `startedAt` and `turn[0].completedAt == turn[1].startedAt`. |
| `thought → tool → thought → text` ordering preserved | `AgentSessionSpecs.MohistPrompt_ThoughtToolThoughtTextSequence_AssistantPartsPreserveEmittedOrder` (lines 453-517) asserts `partTypes == ["reasoning","tool","reasoning","text"]` and cross-index relations. |
| Tool parts preserve raw input, output, metadata, details, status, first-observed position | `AgentSessionSpecs.RunnerExecutesAgentWork_ToolCallUpdate_PreservesFirstObservedIndexAndMergesRawPayload` (lines 147-227) and `RunnerExecutesAgentWork_PendingToolCallUpdate_DoesNotOverwriteTerminalStatus` (lines 229-291) assert `toolParts[0] == tool-1` (preserved index), `rawInput`, `rawOutput`, `metadata.bytes == 5`, `details.format == "markdown"`, and that a `pending` update does not overwrite `completed` status / `title` / `output`. |
| Failed / cancelled / liveness historical events visible after refresh | `AgentSessionSpecs.RunnerReportsTerminalFailure_TerminalEventProjectsAsClosingErrorPartWithFailureReason` (lines 519-547), `RunnerReportsLivenessTransitions_ProjectedAsRecoveryPartsInEventOrder` (lines 549-594), `RunnerReportsCoderRecoveryTransitions_ProjectedAsRecoveryPartsWithLiveMapping` (lines 596-642), and `TerminalEvent_RefreshReplay_ProducesSameClosingPartWithoutSseStream` (lines 644-675) assert that the same closing part is returned by two consecutive reads with no SSE stream. |
| Historical sessions without `mohist_prompt` show explicit `legacy-missing` state | `AgentSessionSpecs.HistoricalSessionWithoutMohistPrompt_ProjectsSingleLegacyMissingTurn` (lines 702-771) and `WorkflowSessionSpecs.GivenLegacySessionWithoutMohistPrompt_WhenIssueWorkflowSessionIsQueried_ThenTranscriptReturnsLegacyMissingTurn` (lines 148-210) assert `user.kind == "legacy-missing"`, `user.text == "Prompt was not recorded for this historical session"`, and `user.text != session.Title/SessionName/Id`. |
| Web tests cover full prompt expansion / copy and raw tool payload visibility | `SessionPage.transcript.test.tsx` adds "expands to reveal the full real mohist_prompt text, not the short task title" (lines 335-377) and "copies the full real mohist_prompt text via the copy action" (lines 379-417) using a 7500+ character prompt; "raw tool payload disclosure" block (lines 1565-1684) asserts `npm test`, `Delegation`, `child-session-123`, `explore` render through the disclosure. `SessionPage.test.tsx` adds "legacy-missing turn does not use task title as prompt body" (lines 808-836) and "renders two turns in event order when fed two mohist_prompt events" (lines 838-928) and the "raw tool payload disclosure" block (lines 933-1019). `session-transcript-display.test.ts` adds "preserves raw input, output, metadata, and details on tool parts" (lines 377-413) and the edit variant. |
| Backend tests cover prompt reconstruction, multiple turns, ordering, tool merge, legacy-missing | 11 new spec tests across `AgentSessionSpecs.cs` and `WorkflowSessionSpecs.cs` (counted above). All pass: `dotnet test --filter "FullyQualifiedName~AgentSessionSpecs|FullyQualifiedName~WorkflowSessionSpecs"` → Passed: 28, Failed: 0, Skipped: 1. |

## Verification Commands

```bash
# Server build
dotnet build packages/server/src/Mohist.Server/Mohist.Server.csproj
# → Build succeeded. 0 Warning(s) 0 Error(s)

# Issue-47 backend specs
dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj \
  --no-build \
  --filter "FullyQualifiedName~AgentSessionSpecs|FullyQualifiedName~WorkflowSessionSpecs"
# → Passed!  - Failed:     0, Passed:    28, Skipped:     1, Total:    29

# Web tests
cd packages/web && npx vitest run
# → Test Files  34 passed (34)
#   Tests       658 passed (658)

# Full server suite (pre-existing failures only)
dotnet test packages/server/tests/Mohist.Server.Tests/Mohist.Server.Tests.csproj --no-build
# → Failed:     6, Passed:   410, Skipped:     3, Total:   419
#   Same 6 failures reproduce on the pre-issue-47 commit (HEAD~7).
```

<promise>PASS</promise>
