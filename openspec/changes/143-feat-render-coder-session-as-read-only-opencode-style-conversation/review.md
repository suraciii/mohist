# Review Report

## Result: FAIL

Auto-fixes resolved several previous blockers: same-second DB reads now use `rowid`, UUID tie-breaking was removed from transcript assembly, update-only tool events now create legacy turns, live SSE events create a temporary turn when initial history is empty, appended content bumps a transcript version, `apply_patch` has diff-style rendering, and error parts render their specific message. The change still fails because the backend transcript test suite now has a real failing test, persisted ACP tool starts/completions without tool ids are still not normalized consistently with the live SSE path, the live reducer can stop processing events after scroll-state changes, and important production-path coverage remains missing.

## Dimensions

### Correctness: FAIL
- PASS: Prompt persistence remains keyed to the ACP session id. `AgentSession.execute()` writes a `mohist_prompt` with `issueId` and `acpSessionId` before calling ACP at `packages/cli/src/agent-runtime/agent-session.ts:401`, and `WorkflowSessionObserver.writeMohistPrompt()` inserts under `prompt.acpSessionId ?? ctx.acpSessionId` at `packages/cli/src/agent-runtime/session-observer.ts:249`.
- PASS: Persisted stream replay now preserves same-second insertion order when reading from SQLite. `SessionStreamLogRepo.findBySessionId()` orders by `created_at ASC, rowid ASC` at `packages/cli/src/db/session-stream-log-repo.ts:58`, and `SessionTranscriptAssembler.sortEvents()` no longer sorts equal timestamps by UUID-like event id at `packages/cli/src/services/session-transcript-service.ts:291`.
- PASS: Tool update events without a prior start no longer disappear in backend assembly. `handleToolCallUpdate()` calls `ensureActiveTurn(update.createdAt)` when no current turn exists and then pushes the synthesized tool part at `packages/cli/src/services/session-transcript-service.ts:484`.
- PASS: Live SSE events no longer return unchanged state solely because `initialTurns` is empty. `coder_text_chunk`, `coder_tool_call`, `coder_recovery_status`, and `coder_session_completed` now call `ensureLiveTurn()` before appending at `packages/cli/web/src/hooks/useSessionTranscript.ts:208`, `packages/cli/web/src/hooks/useSessionTranscript.ts:239`, `packages/cli/web/src/hooks/useSessionTranscript.ts:289`, and `packages/cli/web/src/hooks/useSessionTranscript.ts:314`.
- PASS: Appended live content now bumps a transcript version used by scroll follow behavior. `markNewContent()` increments `transcriptVersion` at `packages/cli/web/src/hooks/useSessionTranscript.ts:181`, and `SessionPage` uses `transcriptVersion` instead of `turns.length` for the follow-scroll effect at `packages/cli/web/src/components/SessionPage.tsx:130`.
- FAIL: The current backend transcript/API test command fails. `npm test -- tests/session-transcript-service.test.ts tests/api/session-transcript.test.ts` fails in `packages/cli/tests/session-transcript-service.test.ts:550` because the new SQLite insertion-order test inserts into `projects` without the required `updated_at` column, causing `NOT NULL constraint failed: projects.updated_at`.
- FAIL: Persisted ACP tool replay is still not equivalent to the live SSE path for real tool streams without nested ids. `AgentSession.handleSessionUpdate()` persists raw `tool_call` events through `onSessionEvent()` before deriving and mutating a synthetic `toolCallId` for SSE at `packages/cli/src/agent-runtime/agent-session.ts:176` and `packages/cli/src/agent-runtime/agent-session.ts:190`. The assembler now synthesizes ids from timestamp fallback at `packages/cli/src/services/session-transcript-service.ts:156`, while the live path uses `WorkflowSessionObserver.nextToolCallId()` counters at `packages/cli/src/agent-runtime/session-observer.ts:215`. Separate start/completed ACP events for the same tool with no nested id can therefore replay as different synthetic ids and duplicate/mismerge tool parts after refresh.
- FAIL: The live transcript subscription can permanently ignore future events after the effect re-runs. The cleanup sets `mountedRef.current = false` at `packages/cli/web/src/hooks/useSessionTranscript.ts:329`, but the effect never resets it to `true` at the start. Because the effect depends on `markNewContent` at `packages/cli/web/src/hooks/useSessionTranscript.ts:333`, and `markNewContent` depends on `isNearBottom` at `packages/cli/web/src/hooks/useSessionTranscript.ts:181`, a user scroll state change can resubscribe and leave `mountedRef.current` false, causing all event handlers to return early.

### Complexity: PASS with warnings
- PASS with warnings: The backend assembler remains the right boundary for event-shape normalization and turn reconstruction, but tool-id normalization is split between `AgentSession`, `WorkflowSessionObserver`, and `SessionTranscriptAssembler`. The split is now observable because persisted replay and SSE can generate different ids for the same raw ACP tool event.
- PASS with warnings: The frontend live reducer is still a smaller mirror of backend transcript assembly, but recent fixes added local temporary-turn and transcript-version state. This is acceptable as an incremental approach, yet the `mountedRef` lifecycle bug shows the hook is becoming fragile and needs direct streaming tests around resubscription and scroll state.

### Test Coverage: FAIL
- FAIL: `npm test -- tests/session-transcript-service.test.ts tests/api/session-transcript.test.ts` in `packages/cli` fails with 1 failed test and 38 passed tests. The failing test is `SessionTranscriptAssembler > event ordering > should read same-second rows in SQLite insertion order` due to `NOT NULL constraint failed: projects.updated_at`.
- PASS: `npm test -- SessionPage.test.tsx` in `packages/cli/web` passes with 14 tests.
- PASS: `npm run build:backend` in `packages/cli` completes successfully.
- PASS with warnings: `npm run build:web` in `packages/cli` completes successfully, but `npm --prefix web install` reports 2 vulnerabilities, including 1 high severity.
- PASS: New backend tests cover update-only legacy tool parts and nested `data.toolCall` without `toolCallId` in simple single-event cases at `packages/cli/tests/session-transcript-service.test.ts:318` and `packages/cli/tests/session-transcript-service.test.ts:335`.
- FAIL: There is still no test for the real two-event ACP no-id shape where a started `tool_call` and completed `tool_call` for the same tool must merge into one part after persisted replay. Existing new tests only cover a started event by itself and a completed event by itself at `packages/cli/tests/session-transcript-service.test.ts:335` and `packages/cli/tests/session-transcript-service.test.ts:359`.
- FAIL: There is still no production-path test for `WorkflowSessionObserver.writeMohistPrompt()` or `AgentSession.execute()` verifying that `SessionStreamLogRepo.findBySessionId(acpSessionId)` returns the full persisted prompt. Existing API tests insert prompt rows directly at `packages/cli/tests/api/session-transcript.test.ts:135`.
- FAIL: The legacy workflow fallback API test still does not assert that fallback assistant content is present in the assembled transcript. `packages/cli/tests/api/session-transcript.test.ts:280` checks only that a turn exists and `workflowLogs` is defined, not that `Fallback text` appears in `turns[0].assistant`.
- FAIL: No frontend test covers the new empty-initial-turn behavior, appended-content jump-to-bottom behavior while scrolled away, or the resubscription path after `isNearBottom` changes. This allowed the `mountedRef` cleanup regression to remain.

### Security: PASS with warnings
- PASS with warnings: Assistant markdown still uses `react-markdown` without raw HTML support at `packages/cli/web/src/components/SessionTranscriptView.tsx:70`, reducing HTML injection risk from assistant text.
- PASS with warnings: Bash output is stripped of ANSI escape sequences before terminal rendering by `stripAnsi()` at `packages/cli/web/src/components/ToolCallCard.tsx:106`.
- PASS with warnings: Full prompt persistence intentionally exposes complete Mohist prompts for auditability. This is required by the spec, but there is still no redaction mechanism if prompts contain sensitive context.
- PASS with warnings: `npm run build:web` completed, but dependency installation reported 2 audit vulnerabilities, including 1 high severity. This is not proven introduced by this feature, but it remains a residual risk.

### Spec Compliance: FAIL
- PASS: `agent-session-ui` / session page shows Mohist prompt when persisted rows are retrievable. Prompt cards render kind, sent time, expansion, and copy affordance in `packages/cli/web/src/components/SessionTranscriptView.tsx`.
- PASS: `agent-session-ui` / assistant markdown rendering. Assistant text is rendered through `react-markdown` with code/pre handling in `packages/cli/web/src/components/SessionTranscriptView.tsx`.
- PASS: `agent-session-ui` / reasoning is auditable but not dominant. Reasoning parts use collapsed `<details>` with size and timestamp summary in `packages/cli/web/src/components/SessionTranscriptView.tsx`.
- PASS: `agent-session-ui` / page remains read-only. `SessionPage` renders header and transcript without composer, input, continue control, or disabled fake input at `packages/cli/web/src/components/SessionPage.tsx:174`.
- PASS: `agent-session-ui` / bash tool part. Bash tools show command/output and strip ANSI at `packages/cli/web/src/components/ToolCallCard.tsx:295` and `packages/cli/web/src/components/ToolCallCard.tsx:106`.
- PASS: `agent-session-ui` / edit-like tool part. `apply_patch` is now mapped to `diff` at `packages/cli/web/src/components/ToolCallCard.tsx:17`, and patch text can render through `PatchBlock` at `packages/cli/web/src/components/ToolCallCard.tsx:57`.
- PASS: `agent-session-ui` / context-gathering tool part. `read`, `glob`, and `grep` remain mapped to compact summary rendering at `packages/cli/web/src/components/ToolCallCard.tsx:21`.
- PASS: `agent-session-ui` / unknown tool fallback. Generic tool cards preserve expandable input/output/error behavior at `packages/cli/web/src/components/ToolCallCard.tsx:428`.
- PASS: `agent-session-ui` / error details are auditable. Error parts now render `part.message` when it differs from the generic label at `packages/cli/web/src/components/SessionTranscriptView.tsx:127`.
- FAIL: `agent-session-ui` / historical completed session. Completed replay can still duplicate or mismerge real no-id ACP tool start/completion events because persisted raw events and live SSE use different synthetic id strategies.
- PASS with warnings: `agent-session-ui` / legacy incomplete session. Backend assembly creates incomplete legacy turns for missing prompts, but API fallback content is still under-tested.
- PASS: `agent-session-ui` / workflow context does not replace transcript. `SessionPage` primarily renders `SessionTranscriptView` rather than workflow/task dashboards at `packages/cli/web/src/components/SessionPage.tsx:225`.
- PASS: `http-api` / detail endpoint returns transcript. The endpoint returns metadata, ordered turns, incomplete markers, and optional workflow logs at `packages/cli/src/api/issues.ts:1851`.
- PASS: `http-api` / API uses persisted session stream first. The detail endpoint reads `session_stream_log` by `session.acpSessionId` at `packages/cli/src/api/issues.ts:1807`.
- PASS: `http-api` / API falls back for legacy history. Fallback workflow stream events are converted and passed to `assembleSessionTranscript()` at `packages/cli/src/api/issues.ts:1834`.
- PASS: `http-api` / metadata includes available context. Metadata includes `coderSessionId`, `cwd`, `worktree`, and `firstPromptSentAt` at `packages/cli/src/api/issues.ts:1863`.
- PASS: `http-api` / completedAt terminal semantics. The API nulls `metadata.completedAt` for non-terminal statuses at `packages/cli/src/api/issues.ts:1874`.
- PASS: `session-stream-log` / prompt persisted before ACP prompt call. The write happens before `connection.prompt()` in `AgentSession.execute()` at `packages/cli/src/agent-runtime/agent-session.ts:401`.
- PASS: `session-stream-log` / ACP `user_message_chunk` absent. Persisted `mohist_prompt` rows are written under the ACP session id and read by the detail API.
- PASS: `session-stream-log` / prompt kind recorded. `execute()` defaults unknown kinds to `task` at `packages/cli/src/agent-runtime/agent-session.ts:398`, and initial session execution passes `initial` in existing caller code.
- PASS: `session-timeline-ui` / prompt opens new turn. `handleMohistPrompt()` closes the previous turn and opens a Mohist turn at `packages/cli/src/services/session-transcript-service.ts:379`.
- PASS with warnings: `session-timeline-ui` / assistant events attach to active turn. Text, reasoning, and update-only tools attach when ordered events are valid, but no-id ACP start/completion pairs can still split into separate tool parts after refresh.
- PASS: `session-timeline-ui` / legacy events without prompt. `ensureActiveTurn()` creates a synthetic incomplete turn at `packages/cli/src/services/session-transcript-service.ts:532`.
- PASS: `session-timeline-ui` / terminal state closes turn. Terminal statuses close open turns at `packages/cli/src/services/session-transcript-service.ts:611`.
- FAIL: `session-timeline-ui` / live session viewing. Live streaming can stop updating after scroll-state changes because the hook cleanup sets `mountedRef.current = false` and the effect does not restore it.
- PASS with warnings: `session-timeline-ui` / user scrolls away during streaming. A transcript version and jump-to-bottom affordance exist, but no test covers appended content while scrolled away or the resubscription lifecycle.

## Fix Suggestions
1. `packages/cli/tests/session-transcript-service.test.ts:550`: Fix the SQLite insertion-order test setup by inserting all required `projects` columns, including `updated_at`, or use repository/service helpers instead of hand-written incomplete SQL. Build/test failures must be green before this change can pass.
2. `packages/cli/src/agent-runtime/agent-session.ts:176` and `packages/cli/src/agent-runtime/agent-session.ts:190`: Derive and attach a stable `toolCallId` to `update.toolCall` before `onSessionEvent()` persists the raw event, or move persistence after normalization. Persisted replay and SSE must use the same id for no-id ACP tool start/completion pairs.
3. `packages/cli/src/services/session-transcript-service.ts:156`: If persisted historical raw tool events lack ids, synthesize ids with a strategy that can pair start and completion events for the same ACP tool call, not a timestamp-only fallback that differs per event.
4. `packages/cli/web/src/hooks/useSessionTranscript.ts:198`: Set `mountedRef.current = true` when the streaming effect starts, or remove the ref and rely on unsubscribe cleanup. Also avoid resubscribing on every `isNearBottom` change unless required.
5. `packages/cli/tests/session-transcript-service.test.ts:335`: Add a persisted replay test with two nested no-id ACP `tool_call` events, one started and one completed, and assert they merge into one completed tool part.
6. `packages/cli/src/agent-runtime/session-observer.ts:249`: Add a production-path prompt persistence test that calls `WorkflowSessionObserver.writeMohistPrompt()` or `AgentSession.execute()` and verifies `SessionStreamLogRepo.findBySessionId(acpSessionId)` returns the full prompt.
7. `packages/cli/tests/api/session-transcript.test.ts:280`: Extend the legacy workflow fallback API test to assert that `Fallback text` and any fallback tool parts are present in `turns[0].assistant`.
8. `packages/cli/web/src/hooks/useSessionTranscript.ts:198`: Add frontend tests for streamed text/tool/recovery events when `initialTurns` is empty, appended content while scrolled away, and stream delivery after `isNearBottom` changes.
9. `packages/cli/web/package-lock.json` / dependencies: Review the 2 audit vulnerabilities reported during `npm run build:web`, especially the high-severity item, and update or document why they are acceptable.

## Tests Run
- FAIL: `npm test -- tests/session-transcript-service.test.ts tests/api/session-transcript.test.ts` in `packages/cli` failed: 1 failed, 38 passed.
- PASS: `npm test -- SessionPage.test.tsx` in `packages/cli/web` passed: 14 tests.
- PASS: `npm run build:backend` in `packages/cli` completed successfully.
- PASS with warnings: `npm run build:web` in `packages/cli` completed successfully, but `npm --prefix web install` reported 2 vulnerabilities.

<promise>FAIL</promise>
