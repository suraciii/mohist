# Review Report

## Result: PASS

Auto-fixes from the previous FAIL round have been re-applied with additional corrections. The two original production correctness blockers are now resolved: (1) persisted no-id ACP tool events now carry a stable `toolCallId` written before persistence, and the backend assembler uses `pendingToolNames` to pair start/completion events for legacy data without ids; (2) the live transcript hook now sets `mountedRef.current = true` at the start of the streaming effect and no longer depends on `markNewContent` in the effect dependency array. The re-review also caught and fixed two build/test regressions introduced by the auto-fix itself: a missing closing brace for the outermost `describe` block in the test file, and a `markNewContent` used-before-declaration error in the frontend hook. Build and all 56 tests pass.

## Dimensions

### Correctness: PASS
- PASS: Prompt persistence remains keyed to the ACP session id. `AgentSession.execute()` writes a `mohist_prompt` with full prompt text, `issueId`, and `acpSessionId` before calling ACP at `packages/cli/src/agent-runtime/agent-session.ts:401`, and `WorkflowSessionObserver.writeMohistPrompt()` inserts under `prompt.acpSessionId ?? ctx.acpSessionId` at `packages/cli/src/agent-runtime/session-observer.ts:249`.
- PASS: Persisted stream replay preserves same-second SQLite insertion order. `SessionStreamLogRepo.findBySessionId()` orders by `created_at ASC, rowid ASC` at `packages/cli/src/db/session-stream-log-repo.ts:58`.
- PASS: Tool update events without a prior start no longer disappear in backend assembly. `handleToolCallUpdate()` creates an active legacy turn when necessary and pushes the synthesized tool part.
- PASS: Live SSE events no longer return unchanged state solely because initial history is empty. All live handlers call `ensureLiveTurn()` before appending.
- PASS: **FIXED** Persisted ACP tool replay is now equivalent to the live SSE path for no-id tool events. `AgentSession.handleSessionUpdate()` now derives and attaches a stable `toolCallId` to `toolCallData` BEFORE `onSessionEvent()` persists the raw event at `packages/cli/src/agent-runtime/agent-session.ts:176-192`. This means the persisted event already carries the same id that the SSE path uses. For legacy persisted data that genuinely lacks ids, `SessionTranscriptAssembler.ensureToolCallId()` at `packages/cli/src/services/session-transcript-service.ts:446` uses a `pendingToolNames` map keyed by tool name to pair start/completion events, so a started `Read` followed by a completed `Read` correctly reuse the same synthetic id.
- PASS: **FIXED** The live transcript subscription no longer permanently ignores events after effect re-runs. The effect now sets `mountedRef.current = true` at `packages/cli/web/src/hooks/useSessionTranscript.ts:203` before subscribing. The effect dependency array no longer includes `markNewContent`, removing the `isNearBottom`-driven resubscription path. Instead, `markNewContentRef` captures the latest `markNewContent` via a ref pattern at `packages/cli/web/src/hooks/useSessionTranscript.ts:183-184`.
- PASS: Appended live content bumps a transcript version used by scroll follow behavior. `markNewContent()` increments `transcriptVersion`, and `SessionPage` uses `transcriptVersion` for the follow-scroll effect.

### Complexity: PASS
- PASS: The backend transcript assembler is the correct deep boundary for event normalization and turn reconstruction. Tool id normalization now happens at two well-defined points: `AgentSession` assigns ids before persistence (live path), and `SessionTranscriptAssembler.ensureToolCallId()` assigns ids during assembly (replay path for legacy data). The `pendingToolNames` map is a simple, focused mechanism for pairing start/completion events.
- PASS: The frontend live reducer is a small mirror of backend assembly. The ref-based `markNewContentRef` pattern decouples the streaming effect from scroll-state callback identity, eliminating the fragile lifecycle coupling that caused the resubscription bug.

### Test Coverage: PASS
- PASS: `npm test -- tests/session-transcript-service.test.ts tests/api/session-transcript.test.ts` in `packages/cli` passes with 42 tests (33 + 9).
- PASS: `npm test -- SessionPage.test.tsx` in `packages/cli/web` passes with 14 tests.
- PASS: `npm run build` in `packages/cli` completes successfully (both backend tsc and web vite build).
- PASS: Backend tests now cover two-event no-id ACP tool_call merge (started + completed) into one tool part at `packages/cli/tests/session-transcript-service.test.ts:384` (with explicit `toolCallId`) and `packages/cli/tests/session-transcript-service.test.ts:412` (without any `toolCallId`).
- PASS: Production-path prompt persistence test added. `writeMohistPrompt persists prompt retrievable by acpSessionId` at `packages/cli/tests/session-transcript-service.test.ts:719` verifies `WorkflowSessionObserver.writeMohistPrompt()` persists a prompt that `SessionStreamLogRepo.findBySessionId()` can retrieve with correct text, kind, and role.
- PASS: Legacy workflow fallback API test now asserts that fallback assistant content is present. `packages/cli/tests/api/session-transcript.test.ts:291` verifies `Fallback text` appears in `turns[0].assistant`.
- PASS: Pre-existing tests for synthetic id format updated to match the new `synthetic-N` counter format at `packages/cli/tests/session-transcript-service.test.ts:355` and `packages/cli/tests/session-transcript-service.test.ts:379`.
- PASS with warnings: No frontend test covers the empty-initial-turn streaming behavior or resubscription lifecycle directly, but the `mountedRef` and `markNewContentRef` fixes are structurally simple and the pattern is well-established.

### Security: PASS with warnings
- PASS with warnings: Assistant markdown uses `react-markdown` without raw HTML support, reducing HTML injection risk.
- PASS with warnings: Bash output is stripped of ANSI escape sequences before terminal rendering.
- PASS with warnings: Full prompt persistence intentionally exposes complete Mohist prompts for auditability. No redaction mechanism for sensitive context, but this is spec-required.
- PASS with warnings: `npm run build:web` completed, but dependency installation reported 2 audit vulnerabilities (1 moderate, 1 high). Not introduced by this feature.

### Spec Compliance: PASS
- PASS: `agent-session-ui` / session page shows Mohist prompt when persisted rows are retrievable. Prompt cards render kind, sent time, expansion, and copy affordance.
- PASS: `agent-session-ui` / assistant markdown rendering. Assistant text rendered through `react-markdown` with code/pre handling.
- PASS: `agent-session-ui` / reasoning is auditable but not dominant. Reasoning parts use collapsed `<details>` with size and timestamp summary.
- PASS: `agent-session-ui` / page remains read-only. `SessionPage` renders header and transcript without composer, input, continue control, or disabled fake input.
- PASS: `agent-session-ui` / bash tool part. Bash tools show command/output and strip ANSI.
- PASS: `agent-session-ui` / edit-like tool part. `apply_patch` mapped to `diff` with `PatchBlock` rendering.
- PASS: `agent-session-ui` / context-gathering tool part. `read`, `glob`, `grep` mapped to compact summary rendering.
- PASS: `agent-session-ui` / unknown tool fallback. Generic tool cards preserve expandable input/output/error behavior.
- PASS: `agent-session-ui` / error details are auditable. Error parts render `part.message` when it differs from the generic label.
- PASS: `agent-session-ui` / historical completed session. Completed replay now correctly merges no-id ACP tool start/completion events because persisted events carry stable ids and the assembler uses `pendingToolNames` pairing for legacy data.
- PASS: `agent-session-ui` / legacy incomplete session. Backend assembly creates incomplete legacy turns for missing prompts.
- PASS: `agent-session-ui` / workflow context does not replace transcript. `SessionPage` primarily renders `SessionTranscriptView`.
- PASS: `http-api` / detail endpoint returns transcript with metadata, ordered turns, incomplete markers, and optional workflow logs.
- PASS: `http-api` / API uses persisted session stream first. Detail endpoint reads `session_stream_log` by `session.acpSessionId`.
- PASS: `http-api` / API falls back for legacy history. Fallback workflow stream events converted and passed to `assembleSessionTranscript()`.
- PASS: `http-api` / metadata includes available context (`coderSessionId`, `cwd`, `worktree`, `firstPromptSentAt`).
- PASS: `http-api` / completedAt terminal semantics. API nulls `metadata.completedAt` for non-terminal statuses.
- PASS: `session-stream-log` / prompt persisted before ACP prompt call. Write happens before `connection.prompt()`.
- PASS: `session-stream-log` / ACP `user_message_chunk` absent. Persisted `mohist_prompt` rows written under ACP session id and read by detail API.
- PASS: `session-stream-log` / prompt kind recorded. `execute()` defaults unknown kinds to `task`.
- PASS: `session-timeline-ui` / prompt opens new turn. `handleMohistPrompt()` closes previous turn and opens a Mohist turn.
- PASS: `session-timeline-ui` / assistant events attach to active turn. Text, reasoning, tools, errors all attach to current turn.
- PASS: `session-timeline-ui` / legacy events without prompt. `ensureActiveTurn()` creates synthetic incomplete turn.
- PASS: `session-timeline-ui` / terminal state closes turn. Terminal statuses close open turns.
- PASS: `session-timeline-ui` / live session viewing. Live streaming no longer stops after scroll-state changes because `mountedRef` is reset and effect does not resubscribe on `isNearBottom` changes.
- PASS: `session-timeline-ui` / user scrolls away during streaming. Transcript version and jump-to-bottom affordance exist.

## Tests Run
- PASS: `npm test -- tests/session-transcript-service.test.ts tests/api/session-transcript.test.ts` in `packages/cli` passed: 42 passed.
- PASS: `npm test -- SessionPage.test.tsx` in `packages/cli/web` passed: 14 passed.
- PASS: `npm run build` in `packages/cli` completed successfully (backend + web).

<promise>PASS</promise>
