# Review Report

## Result: FAIL

The implementation adds a transcript assembler, session detail API shape, transcript UI, and targeted tests, but it does not satisfy the end-to-end persistence and replay requirements. Error-level issues in prompt persistence, legacy fallback assembly, real ACP tool-event parsing, and live streaming behavior make the overall result FAIL.

## Dimensions

### Correctness: FAIL

`mohist_prompt` events are written under a session key that the detail API never reads, legacy workflow fallback logs are not passed into transcript assembly, real ACP tool-call payloads do not match the parser shape, and live pages can drop streamed events when no turn exists yet.

### Complexity: PASS with warnings

The implementation introduces an authoritative backend assembler, which is the right module boundary, but `SessionTranscriptAssembler` is large and stateful (`packages/cli/src/services/session-transcript-service.ts:198-548`) and uses nondeterministic IDs (`packages/cli/src/services/session-transcript-service.ts:94-96`). This is not itself an acceptance blocker, but it increases replay and maintenance risk.

### Test Coverage: FAIL

There are targeted tests for the assembler and API (`packages/cli/tests/session-transcript-service.test.ts`, `packages/cli/tests/api/session-transcript.test.ts`), and they pass. However, coverage misses the real `writeMohistPrompt` persistence key, misses fallback text/tool assertions for workflow-log legacy data, and uses normalized fake tool events rather than the actual nested ACP `toolCall` shape.

### Security: PASS with warnings

Assistant text uses `react-markdown` without raw HTML support in `packages/cli/web/src/components/SessionTranscriptView.tsx:70-97`, and bash output is ANSI-stripped in `packages/cli/web/src/components/ToolCallCard.tsx:62-64`. The main warning is that newly persisted full prompts may expose sensitive audit context by design; the UI makes prompts collapsible and copyable, but no additional redaction mechanism is present.

### Spec Compliance: FAIL

Several required scenarios fail with concrete evidence in the compliance matrix below, especially Mohist prompt replay, legacy fallback replay, historical tool replay, `apply_patch` rendering, complete metadata context, and live scroll/new-content behavior.

## Changed Files Covered

- `packages/cli/src/agent-runtime/agent-session.ts`
- `packages/cli/src/agent-runtime/session-observer.ts`
- `packages/cli/src/api/issues.ts`
- `packages/cli/src/db/coder-session-repo.ts`
- `packages/cli/src/db/session-stream-log-repo.ts`
- `packages/cli/src/services/session-transcript-service.ts`
- `packages/cli/web/src/components/SessionPage.tsx`
- `packages/cli/web/src/components/SessionTranscriptView.tsx`
- `packages/cli/web/src/components/ToolCallCard.tsx`
- `packages/cli/web/src/hooks/useSessionTranscript.ts`
- `packages/cli/web/src/lib/types.ts`
- `packages/cli/tests/session-transcript-service.test.ts`
- `packages/cli/tests/api/session-transcript.test.ts`

## Error Findings

### 1. Mohist prompts are persisted under the wrong session id

Verdict: FAIL

Evidence: `packages/cli/src/agent-runtime/agent-session.ts:395-403` writes the prompt before `connection.prompt`, but `WorkflowSessionObserver.writeMohistPrompt` calls `sessionStreamLogRepo.insert(prompt.executionId ?? this._coderSessionId ?? '', prompt.executionId ?? '', 'mohist_prompt', prompt)` at `packages/cli/src/agent-runtime/session-observer.ts:247-255`. The repository signature is `insert(issueId, sessionId, eventType, data)` at `packages/cli/src/db/session-stream-log-repo.ts:36-44`, and the detail API reads prompt/history events with `sessionStreamLogRepo.findBySessionId(session.acpSessionId)` at `packages/cli/src/api/issues.ts:1806-1809`. Assistant/tool stream events are inserted under `ctx.acpSessionId` at `packages/cli/src/agent-runtime/session-observer.ts:178-184`.

Impact: normal session detail replay misses `mohist_prompt` rows, so the page can still lose Mohist prompts after refresh and reconstruct assistant/tool events as legacy incomplete turns.

Suggested fix: in `packages/cli/src/agent-runtime/session-observer.ts:247-255`, insert prompt rows with the issue id and ACP session id, not execution id. Add `issueId` and `acpSessionId` to `MohistPromptEvent` or pass a `SessionContext` into `writeMohistPrompt`, then call `sessionStreamLogRepo.insert(ctx.issueId, ctx.acpSessionId, 'mohist_prompt', prompt)`. Add a test that calls `writeMohistPrompt` or `AgentSession.execute` and asserts `sessionStreamLogRepo.findBySessionId(acpSessionId)` returns the prompt.

### 2. Legacy workflow-log fallback is collected but not assembled

Verdict: FAIL

Evidence: `packages/cli/src/api/issues.ts:1811-1829` builds `fallbackLogs` when `session_stream_log` is empty, but `packages/cli/src/api/issues.ts:1831` still calls `assembleSessionTranscript(session, streamEvents)`. In the fallback case `streamEvents` is empty, so the assembler cannot attach available legacy assistant/tool events. The test at `packages/cli/tests/api/session-transcript.test.ts:280-291` checks `turns.length` and `workflowLogs`, but does not assert that `Fallback text` is present in `turns[0].assistant`.

Impact: historical sessions that only have legacy `workflow_log` stream events can show only `Prompt was not recorded for this historical session` without the available coder output or tool parts.

Suggested fix: in `packages/cli/src/api/issues.ts:1811-1831`, convert `fallbackLogs` into `SessionStreamLogEntry`-compatible objects and pass `streamEvents.length > 0 ? streamEvents : fallbackStreamEvents` to `assembleSessionTranscript`. Update `packages/cli/tests/api/session-transcript.test.ts:280-291` to assert the fallback assistant text/tool parts appear in the returned turn.

### 3. Historical tool replay parses the wrong ACP payload shape

Verdict: FAIL

Evidence: `packages/cli/src/agent-runtime/agent-session.ts:176-183` persists the raw ACP `update` object through `onSessionEvent`. For tool calls, `packages/cli/src/agent-runtime/agent-session.ts:190-207` reads tool details from `update.toolCall`. However, `parseToolCallStart` expects top-level `toolCallId`, `toolName`, and `input` at `packages/cli/src/services/session-transcript-service.ts:142-151`, and `parseToolCallUpdate` expects top-level `toolCallId`, `status`, and `output` at `packages/cli/src/services/session-transcript-service.ts:154-166`.

Impact: persisted real ACP tool events can be skipped during completed-session replay, so historical sessions do not reliably show tool parts inside the corresponding assistant turn.

Suggested fix: update `packages/cli/src/services/session-transcript-service.ts:142-166` to normalize both the current test shape and the real ACP shape under `data.toolCall`. Use `data.toolCall.toolCallId` when available, or derive a stable id consistently with `AgentSession.handleNotification` if ACP lacks one. Add tests using the exact nested `toolCall` payload that `AgentSession.handleNotification` persists.

### 4. Live transcript drops streaming events when no turn exists

Verdict: FAIL

Evidence: `packages/cli/web/src/hooks/useSessionTranscript.ts:173-175` returns unchanged state for `coder_text_chunk` when `prev.length === 0`. `packages/cli/web/src/hooks/useSessionTranscript.ts:204-205` and `packages/cli/web/src/hooks/useSessionTranscript.ts:226-227` do the same for tool events. The design allows the first prompt to appear only after persisted-history refetch if prompt SSE is not emitted, so streamed assistant parts can be dropped before the first turn exists.

Impact: a live session detail page can show `Waiting for activity...` or miss streamed output until refresh, violating the live transcript and streaming-readability requirements.

Suggested fix: in `packages/cli/web/src/hooks/useSessionTranscript.ts:168-238`, when a matching text/tool event arrives and `prev.length === 0`, either invalidate/refetch `['issues', issueNumber, 'coder-sessions', sessionId]` immediately or create a temporary incomplete turn that reconciles with backend transcript after refetch. Prefer also emitting persisted Mohist prompt events over SSE so the first turn exists before assistant events arrive.

## Warning Findings

### 1. Metadata omits required session context

Verdict: PASS with warnings

Evidence: `SessionMetadata` contains `sessionId`, `issueId`, `acpSessionId`, `executionId`, `title`, `status`, `model`, `stage`, `createdAt`, and `completedAt` at `packages/cli/src/services/session-transcript-service.ts:6-17`. The response mirrors that at `packages/cli/src/api/issues.ts:1848-1859`. Required context such as `cwd`/worktree, a clear `coderSessionId` field, and first prompt sent time is missing.

Suggested fix: extend `SessionMetadata` in `packages/cli/src/services/session-transcript-service.ts:6-17` and the API response at `packages/cli/src/api/issues.ts:1848-1859` with `coderSessionId`, `cwd` or worktree path from session start metadata, and `firstPromptSentAt` when available.

### 2. Transcript IDs are unstable across replay

Verdict: PASS with warnings

Evidence: `generateId()` uses `Date.now()` and `Math.random()` at `packages/cli/src/services/session-transcript-service.ts:94-96`, so turn and part IDs change on every historical reconstruction.

Suggested fix: derive IDs from event ids and stable per-event indexes in `packages/cli/src/services/session-transcript-service.ts`, such as `turn-${event.id}` and `part-${event.id}-${index}`.

### 3. Scroll/new-content tracking ignores updates inside a turn

Verdict: PASS with warnings

Evidence: new content detection compares only `turns.length` at `packages/cli/web/src/hooks/useSessionTranscript.ts:300-315`, and auto-scroll reacts to `turns.length` at `packages/cli/web/src/components/SessionPage.tsx:127-137`. Streaming text appended to an existing text part does not change `turns.length`.

Suggested fix: track a monotonically increasing transcript version or last-part/text length in `packages/cli/web/src/hooks/useSessionTranscript.ts`, and use that value for new-content detection and auto-scroll effects in `packages/cli/web/src/components/SessionPage.tsx`.

### 4. `apply_patch` is not classified as edit-like

Verdict: PASS with warnings

Evidence: `TOOL_DISPLAY_TYPE` maps `edit` and `write` to `diff`, but not `apply_patch`, at `packages/cli/web/src/components/ToolCallCard.tsx:16-28`.

Suggested fix: add `apply_patch: 'diff'` at `packages/cli/web/src/components/ToolCallCard.tsx:16-28` and extend `parseEditInput` at `packages/cli/web/src/components/ToolCallCard.tsx:30-42` to produce a useful patch-style summary when input is not `oldString`/`newString` based.

### 5. Error UI hides the detailed error message

Verdict: PASS with warnings

Evidence: `SessionErrorPartView` receives `part.message`, but renders only a generic label and timestamp at `packages/cli/web/src/components/SessionTranscriptView.tsx:115-133`.

Suggested fix: render `part.message` in `packages/cli/web/src/components/SessionTranscriptView.tsx:123-133`, either inline for short messages or in an expandable details block for long messages.

## Tests Run

- FAIL: `npm test -- --runInBand tests/session-transcript-service.test.ts tests/api/session-transcript.test.ts` failed because Vitest does not support `--runInBand`.
- PASS: `npm test -- tests/session-transcript-service.test.ts tests/api/session-transcript.test.ts` passed: 35 tests across 2 files.

## Spec Compliance

### agent-session-ui: Read-only session transcript

- FAIL: Session page shows Mohist prompt. UI prompt cards support collapse/copy at `packages/cli/web/src/components/SessionTranscriptView.tsx:15-67`, but persisted prompts are not found by normal detail lookup because `session-observer.ts:247-255` stores prompt rows under execution id while `issues.ts:1806-1809` reads by ACP session id.
- PASS: Assistant text renders as markdown. `react-markdown` renders assistant text with inline and fenced code handling at `packages/cli/web/src/components/SessionTranscriptView.tsx:70-97`.
- PASS: Reasoning is auditable but not dominant. Reasoning is collapsed by default through `<details>` and shows size/time at `packages/cli/web/src/components/SessionTranscriptView.tsx:100-112`.
- PASS: Page remains read-only. `SessionPage` renders a header, transcript area, and jump button at `packages/cli/web/src/components/SessionPage.tsx:171-241`; it does not render a composer, input box, continue control, or disabled fake input.

### agent-session-ui: Tool parts progressive disclosure

- PASS: Bash tool part. Bash command/output rendering and ANSI stripping are implemented at `packages/cli/web/src/components/ToolCallCard.tsx:62-64` and `packages/cli/web/src/components/ToolCallCard.tsx:247-307`.
- FAIL: Edit-like tool part. `edit` and `write` are mapped to diff rendering at `packages/cli/web/src/components/ToolCallCard.tsx:16-19`, but `apply_patch` is omitted from `TOOL_DISPLAY_TYPE` at `packages/cli/web/src/components/ToolCallCard.tsx:16-28`.
- PASS: Context-gathering tool part. `read`, `glob`, and `grep` map to compact summary rows with expandable input/output at `packages/cli/web/src/components/ToolCallCard.tsx:20-22` and `packages/cli/web/src/components/ToolCallCard.tsx:309-378`.
- PASS: Unknown tool part. Generic fallback renders status, tool name, expandable input/output, and error summary at `packages/cli/web/src/components/ToolCallCard.tsx:380-449`.

### agent-session-ui: Session transcript acceptance

- FAIL: Historical completed session. Prompt events are stored under the wrong session id (`packages/cli/src/agent-runtime/session-observer.ts:247-255`), and real persisted tool events are skipped because parser shape does not match `update.toolCall` (`packages/cli/src/agent-runtime/agent-session.ts:190-207`, `packages/cli/src/services/session-transcript-service.ts:142-166`).
- FAIL: Legacy incomplete session. Synthetic incomplete turns exist at `packages/cli/src/services/session-transcript-service.ts:470-522`, but API fallback workflow events are not fed into the assembler at `packages/cli/src/api/issues.ts:1811-1831`, so available coder output/tool parts can be missing.
- PASS: Workflow context does not replace transcript. `SessionPage` renders session header metadata plus transcript rather than workflow/task dashboards at `packages/cli/web/src/components/SessionPage.tsx:171-241`.

### http-api: Coder session transcript detail

- FAIL: Detail endpoint returns transcript. The endpoint returns metadata and `turns` at `packages/cli/src/api/issues.ts:1797-1863`, but prompt and real tool reconstruction are broken by the persistence key mismatch and parser shape mismatch.
- PASS: API uses persisted session stream first. It reads `session_stream_log` by ACP session id at `packages/cli/src/api/issues.ts:1806-1809`.
- FAIL: API falls back for legacy history. It builds fallback workflow logs at `packages/cli/src/api/issues.ts:1811-1829`, but calls `assembleSessionTranscript(session, streamEvents)` with empty `streamEvents` at `packages/cli/src/api/issues.ts:1831`.
- PASS: Metadata distinguishes running and terminal sessions. The response nulls `metadata.completedAt` for non-terminal status at `packages/cli/src/api/issues.ts:1833-1859`, and repo updates only set `completed_at` for terminal statuses at `packages/cli/src/db/coder-session-repo.ts:153-167`.

### session-stream-log: Mohist prompt persistence

- FAIL: Prompt persisted before ACP prompt call. The write happens before `connection.prompt` at `packages/cli/src/agent-runtime/agent-session.ts:395-418`, but the row is inserted under the wrong session id at `packages/cli/src/agent-runtime/session-observer.ts:247-255`, so it is not retrievable as session history.
- FAIL: ACP `user_message_chunk` absent. A `mohist_prompt` event can be inserted, but normal detail lookup misses it because `packages/cli/src/api/issues.ts:1806-1809` reads by ACP session id while `packages/cli/src/agent-runtime/session-observer.ts:247-255` writes by execution id.
- PASS: Prompt kind recorded. `execute` defaults unknown prompt kind to `task` at `packages/cli/src/agent-runtime/agent-session.ts:392`, the default `withSession` path uses `initial` at `packages/cli/src/agent-runtime/agent-session.ts:579`, and plan/check retry call sites pass `retry` at `packages/cli/src/workflow/plan-stage-runner.ts:216` and `packages/cli/src/workflow/check-stage-runner.ts:214`.

### session-stream-log: Session lifecycle metadata remains trustworthy

- PASS: Running session status update. Insert sets `completed_at` to null at `packages/cli/src/db/coder-session-repo.ts:135-138`, and non-terminal `updateStatus` preserves completed time at `packages/cli/src/db/coder-session-repo.ts:153-167`.
- PASS: Terminal session status update. Terminal statuses set `completed_at` at `packages/cli/src/db/coder-session-repo.ts:153-161`.

### session-timeline-ui: Conversation turn reconstruction

- PASS: Prompt opens new turn. The assembler handles `mohist_prompt` at `packages/cli/src/services/session-transcript-service.ts:263-349`.
- FAIL: Assistant events attach to active turn. Text and reasoning attach at `packages/cli/src/services/session-transcript-service.ts:351-385`, but real persisted ACP tool events do not attach because parsing expects flattened fields at `packages/cli/src/services/session-transcript-service.ts:142-166` instead of the real nested shape from `packages/cli/src/agent-runtime/agent-session.ts:190-207`.
- FAIL: Legacy events without prompt. The assembler supports synthetic legacy turns at `packages/cli/src/services/session-transcript-service.ts:470-522`, but the API does not pass legacy workflow fallback events into the assembler at `packages/cli/src/api/issues.ts:1811-1831`.
- PASS: Terminal state closes turn. Terminal statuses close open turns at `packages/cli/src/services/session-transcript-service.ts:234-242` and `packages/cli/src/services/session-transcript-service.ts:543-546`.

### session-timeline-ui: Live and historical transcript replay

- FAIL: Refresh live session. Refresh relies on persisted prompt and tool history, but prompt lookup misses `mohist_prompt` rows and real persisted tool events are skipped.
- FAIL: Completed session replay. Completed replay does not reliably show prompts/tools because of the same prompt persistence and real ACP tool parsing failures.
- FAIL: User scrolls away during streaming. A jump-to-bottom button exists at `packages/cli/web/src/components/SessionPage.tsx:52-64` and `packages/cli/web/src/components/SessionPage.tsx:238-240`, but new-content detection and auto-scroll only watch `turns.length` at `packages/cli/web/src/hooks/useSessionTranscript.ts:300-315` and `packages/cli/web/src/components/SessionPage.tsx:127-137`, so streamed updates inside an existing turn are not handled correctly.

## Placeholder Check

PASS. No placeholder text such as `[findings]`, `[TODO]`, or `[placeholder]` remains.

## Reasoning Process Check

PASS. The report contains findings, evidence, verdicts, and fix suggestions only; it does not include private thinking or reasoning process.

<promise>FAIL</promise>
