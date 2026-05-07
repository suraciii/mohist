## Context

Mohist already has a dedicated session route, `GET /api/issues/:number/coder-sessions`, `coder_session` rows, `session_stream_log`, and frontend reconstruction in `useSessionTimeline`. The current model is still log-shaped: it relies on `user_message_chunk` when available, reconstructs `Round` objects in the browser, stores assistant/tool stream events but not Mohist prompts at the `AgentSession.execute(prompt)` boundary, and renders assistant output as plain pre-wrapped text with tool cards attached to a round.

The target user experience is a read-only session transcript modeled after opencode's session information architecture: each Mohist prompt is the user-side message for a turn, the coder agent response consists of assistant parts, and tool calls/thinking/errors are inline parts within that assistant turn. Workflow, task, check, and diff information remain useful context, but the session page's primary artifact is the coder session conversation.

The opencode reference files named in the issue are not present in this checkout (`opensrc/opencode` is empty), so this design uses the issue's documented opencode concepts rather than copying source structure.

## Goals / Non-Goals

**Goals:**

- Persist every Mohist prompt sent to the coder agent so session replay does not depend on ACP emitting `user_message_chunk`.
- Provide a stable transcript data model that groups Mohist messages, coder text, reasoning, tools, errors, and recovery events by conversation turn.
- Make session detail historical replay and live streaming converge on the same ordering and rendering semantics.
- Render `/issue/:number/session/:sessionId` as a read-only opencode-style transcript with markdown assistant text, collapsible prompts/reasoning, inline tool parts, and trustworthy session metadata.
- Preserve legacy sessions by creating an explicit incomplete-data synthetic turn when no persisted Mohist prompt exists.

**Non-Goals:**

- Do not implement a composer or continue-conversation input.
- Do not turn session detail into a workflow/task/check/diff workbench.
- Do not migrate old `workflow_log` rows into `session_stream_log`.
- Do not copy opencode's SolidJS implementation or introduce opencode as a runtime dependency.
- Do not truncate prompt, assistant text, or tool output at persistence time for UI convenience.

## Decisions

### D1: Persist Mohist prompts as Mohist-owned session stream events

`AgentSession.execute(prompt)` will record a new session stream event before calling `connection.prompt`. Use a Mohist-owned event type such as `mohist_prompt` rather than relying on ACP `user_message_chunk`.

The event data should include:

```ts
type MohistPromptEvent = {
  role: 'mohist'
  text: string
  kind: 'initial' | 'task' | 'retry' | 'followup' | 'recovery'
  executionId?: string
  stage?: string
  title?: string
  sentAt: string
}
```

Prompt `kind` should come from `AgentSession.execute(prompt, meta?)` or a small `ExecutePromptOptions` parameter. If caller metadata is not yet available, default to `task` and infer better labels from stage/execution context as a fallback. `withSession(options)` should pass `initial` for `options.task`; plan/check/review retry call sites should pass `retry` or `followup` where they know the semantics.

**Alternatives considered:** Rely only on ACP `user_message_chunk`. This keeps fewer Mohist-specific events, but it is explicitly unreliable and loses the main audit input. Store prompts only on `coder_session`. This works for single-prompt sessions but fails for multi-prompt retry/follow-up sessions and makes turn boundaries ambiguous.

### D2: Build a backend transcript assembler as the deep module boundary

Add a session transcript assembler in the backend, for example `packages/cli/src/services/session-transcript-service.ts` or `packages/cli/src/agent-runtime/session-transcript.ts`. Its interface should be simple: given a `CoderSession` plus ordered stream/workflow fallback logs, return a complete `SessionTranscript`.

```ts
type SessionTranscript = {
  session: SessionMetadata
  turns: SessionTurn[]
  rawEvents?: WorkflowLogItem[]
  incomplete: boolean
}

type SessionTurn = {
  id: string
  startedAt: string
  completedAt: string | null
  incomplete?: boolean
  user: {
    role: 'mohist'
    text: string
    kind: 'initial' | 'task' | 'retry' | 'followup' | 'recovery' | 'legacy-missing'
    sentAt: string
  }
  assistant: SessionPart[]
}

type SessionPart =
  | { id: string; type: 'text'; text: string; startedAt: string; completedAt?: string | null }
  | { id: string; type: 'reasoning'; text: string; startedAt: string; completedAt?: string | null }
  | { id: string; type: 'tool'; tool: ToolPart }
  | { id: string; type: 'error'; message: string; kind: 'timeout' | 'failed' | 'cancelled' | 'recovery'; at: string }
```

The assembler owns ACP/Mohist event normalization, turn boundary rules, tool call start/update merging, legacy fallback, and completed-at calculation. The frontend should not need to know which raw event types open turns or how to merge `tool_call` and `tool_call_update`.

**Alternatives considered:** Continue reconstructing in `useSessionTimeline`. That is faster to modify but leaks persistence semantics into UI, increases duplication between history and live paths, and makes legacy behavior fragile. Return only raw logs and add helpers in the browser. This preserves API compatibility but keeps the current root problem: the page guesses conversation structure.

### D3: Keep `session_stream_log` append-only and avoid schema changes unless metadata requires indexing

Persist `mohist_prompt` and session-local error/recovery events into `session_stream_log` using the existing JSON `data` column. Do not add a separate `session_message` table for this change. Add event type handling and repo helpers rather than a new persistence model.

If exact ordering becomes ambiguous because `created_at` uses second precision, add an ordering improvement to `session_stream_log`, such as storing ISO timestamps in event data and ordering by `created_at, id`, or a future integer sequence column. The design prefers not adding a migration unless tests show multiple events in the same second reorder incorrectly.

**Alternatives considered:** Add normalized `session_messages` and `session_parts` tables. This would make queries semantically clean, but it is a larger migration and duplicates the already append-only stream source. Store transcript snapshots. Snapshots speed reads but create invalidation problems for live sessions and raw audit events.

### D4: Expose transcript data through the coder session API while retaining raw logs for debugging

Extend `GET /api/issues/:number/coder-sessions` to include `transcript` for each session, or add `GET /api/issues/:number/coder-sessions/:sessionId` for detail and keep the list endpoint lighter. Prefer adding a detail endpoint if payload size becomes large, while keeping the existing list endpoint compatible for `SessionList`.

Recommended shape for detail:

```ts
type CoderSessionDetail = CoderSessionItem & {
  metadata: SessionMetadata
  transcript: SessionTranscript
  workflowLogs?: WorkflowLogItem[]
}
```

`SessionPage` should fetch the selected session detail directly. `useCoderSessions` can continue powering session lists and live lifecycle updates. This avoids loading every long transcript just to find one session.

**Alternatives considered:** Put transcript on every `CoderSessionItem` in the list endpoint. This is easiest for the current UI but scales poorly for issues with many long sessions. Replace existing `workflowLogs` immediately. This is cleaner but risks breaking other components; keeping raw logs during transition reduces coupling.

### D5: Define deterministic turn boundaries in one place

The assembler will apply these rules:

- `mohist_prompt` opens a new turn.
- ACP `user_message_chunk` opens a turn only when there is no adjacent Mohist-owned prompt for the same prompt; otherwise it is ignored or attached as redundant raw input.
- When a new prompt opens, the previous open turn completes at the new prompt timestamp.
- Assistant text chunks append to the current turn's active text part.
- Thought chunks append to the current turn's active reasoning part.
- Tool starts create tool parts in the current turn; tool updates merge into the matching part by stable `toolCallId`.
- Timeout, failure, cancellation, recovery, and resume events become `error` parts in the current turn and may complete the turn if terminal.
- Session terminal status completes the current turn at `completedAt`.
- Assistant/tool events before any prompt create a synthetic legacy turn with Mohist text `Prompt was not recorded for this historical session` and `incomplete: true`.

**Alternatives considered:** Derive turns from stage/round metadata. That makes workflow structure primary again and incorrectly merges multi-prompt sessions. Derive turns from time gaps. This is brittle and impossible to explain to users.

### D6: Fix session lifecycle metadata semantics at the repo boundary

`CoderSessionRepo.updateStatus` currently always sets `completed_at`, regardless of status. Split status updates so `completed_at` is only set for terminal statuses: `completed`, `failed`, `timeout`, and `cancelled`. Running or non-terminal updates must preserve `completed_at = NULL`.

Session header metadata should prefer `title`, then execution/task label, then stage label, then a compact fallback. Include `cwd` or worktree path from session start metadata if available, plus `coderSessionId`, `acpSessionId`, `executionId`, model, stage, process/status, created time, completed time only when terminal, and first prompt sent time when known.

**Alternatives considered:** Leave lifecycle semantics to the frontend. This risks showing misleading completed times and duplicates status rules in UI. Store all metadata only inside stream events. This loses simple queryability for session lists.

### D7: Replace frontend rounds with transcript components

Create transcript-focused components and types in the WebUI:

- `SessionTranscriptView` renders the list of turns.
- `SessionTurnView` renders one Mohist prompt card followed by coder assistant parts.
- `MohistPromptCard` shows prompt kind, timestamp, collapsed preview, full expansion, and copy action.
- `AssistantTextPart` renders markdown.
- `ReasoningPart` is collapsed by default with size and timestamp.
- `ToolPart` delegates to specialized tool displays or generic fallback.
- `SessionErrorPart` renders timeout, failure, cancellation, recovery, and retry/recovery messages inline.

`SessionPage` remains the route-level composition: header, transcript scroll container, and optional raw/details disclosure. It should no longer define `ConversationRound` as the core display unit.

**Alternatives considered:** Evolve `Round` to carry more fields. The name and shape encode workflow/round thinking and will continue to pull the implementation away from session turns. A clean transcript type reduces cognitive load for future composer support.

### D8: Use markdown and tool rendering as presentation-only adapters

Assistant text parts should render through a markdown component with support for headings, lists, inline code, fenced code blocks, and horizontally scrollable code. Tool input/output rendering should be adapters over `ToolPart`, not part of transcript assembly.

Tool display classification:

- `bash`: show command row, status, duration if available, and terminal output with ANSI stripped or safely rendered.
- `edit`, `write`, `apply_patch`: show path/target and a diff-like summary when input supports it; otherwise fall back to expandable raw input/output.
- `read`, `grep`, `glob`: show compact context-gathering rows with target/pattern and expandable details.
- unknown tools: show generic name/status/target plus expandable input/output/error.

Large content should remain persisted in full. The UI defaults to collapsed previews with line/byte counts and renders expanded content lazily enough to avoid blocking the first paint.

**Alternatives considered:** Store pre-rendered HTML/markdown in the backend. This couples API to presentation and increases XSS risk. Add a separate tool component per tool without a generic fallback. This risks losing unknown tools, which the issue explicitly forbids.

### D9: Live streaming updates should append transcript events, then reuse the same assembler rules client-side only for new events

Historical load should come from the backend transcript. For live sessions, the browser can append SSE events to the current transcript using a small client-side mirror of the same event reducer, or it can periodically refetch detail. Prefer a reducer for responsive streaming and a refetch on lifecycle completion to reconcile with persisted history.

The live reducer should process `coder_text_chunk`, `coder_tool_call`, `plan_session_update`, and new prompt events if emitted. If prompt events are not emitted over SSE in the first implementation, the first prompt still appears after refresh from persisted logs, but live session pages should refetch after session start or first activity to pick up persisted prompts.

Auto-scroll follows the active generation only while the user is near the bottom. If the user scrolls away, do not force scroll; show a jump-to-bottom affordance when new content arrives below the viewport.

**Alternatives considered:** SSE-only with no historical refetch. This fails refresh/replay requirements. Refetch-only polling for all streaming text. This is simpler but produces poorer typing feedback and more server load.

## Risks / Trade-offs

- [Risk] Transcript assembly duplicates a subset of live reducer logic in frontend → Keep backend assembly authoritative, keep frontend live reducer small, and reconcile by refetching on terminal lifecycle events.
- [Risk] Event ordering can be wrong for events written in the same second → Use event `sentAt`/timestamp fields and deterministic fallback ordering; add a sequence column later if tests expose ambiguity.
- [Risk] Prompt persistence can record sensitive context that was previously not visible in the UI → This is required for auditability; make the prompt collapsible by default and provide clear copy/expand behavior rather than hiding it.
- [Risk] Long prompts or tool outputs can make session detail slow → Persist full data but render collapsed previews, count lines/bytes, and defer expanded heavy rendering.
- [Risk] Existing historical sessions lack Mohist prompts → Create a synthetic incomplete legacy turn and still show assistant/tool events.
- [Risk] Adding markdown rendering may introduce XSS concerns → Use a React markdown renderer configured without raw HTML, or sanitize raw HTML if HTML support is ever enabled.
- [Risk] Existing `coder-sessions` consumers may expect `workflowLogs` only → Add transcript fields compatibly and keep raw logs during the transition.

## Migration Plan

1. Add `mohist_prompt` to session stream event handling and write it from `AgentSession.execute(prompt)` before sending the ACP prompt.
2. Add optional prompt metadata at known caller sites for initial/task/retry/follow-up/recovery classification; default unknown calls to `task`.
3. Add the backend transcript assembler and unit tests for prompt boundaries, tool merge, thought/text accumulation, terminal events, and legacy missing-prompt fallback.
4. Extend API responses with transcript/detail data while retaining existing fields for compatibility.
5. Update frontend types and route data loading so `SessionPage` consumes transcript data instead of `Round[]`.
6. Replace round rendering with transcript components, markdown assistant rendering, collapsible prompt/reasoning, improved tool parts, and inline error/recovery parts.
7. Update live streaming behavior to append/reconcile transcript parts and preserve auto-scroll only while the user is near the bottom.
8. Fix session lifecycle metadata so running sessions do not display `completedAt`, and header labels prefer meaningful titles.
9. Validate with new unit/component tests and at least one manual run covering a live session, refresh replay, a completed session, and a legacy session with no prompt.

Rollback is low risk because persistence is append-only. If the new UI has issues, keep `workflowLogs` in the API and temporarily render the old round view while leaving `mohist_prompt` events in `session_stream_log`. Removing the new event type is not necessary; older code will ignore unknown events.

## Open Questions

- Should the detail API be added as `GET /api/issues/:number/coder-sessions/:sessionId`, or should `GET /api/issues/:number/coder-sessions` include transcript only for the selected session via query parameter?
- Should `mohist_prompt` events be emitted over SSE immediately, or is persisted history plus refetch sufficient for the first implementation?
- Do we need an explicit `sequence` column in `session_stream_log` now, or can deterministic ordering by timestamp plus row id satisfy current SQLite behavior?
