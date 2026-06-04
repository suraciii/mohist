# OpenSpec Capability: agent-session-ui

### Requirement: WebUI subscribes to agent detail SSE events

The WebUI SSE subscription SHALL include the following event types: `agent_text_chunk`, `main_tool_call`, `coder_text_chunk`, `coder_tool_call`, `ralph_task_update`, `ralph_loop_progress`, `plan_round_start`, `plan_session_update`. All 4 registration arrays must be kept in sync: `event-bus.ts` EventMap, `events.ts` ALL_EVENT_TYPES, `agent-events.ts` AGENT_DETAIL_EVENTS, `useSSE.ts` eventTypes.

#### Scenario: Agent starts and streams text
- **WHEN** an agent session emits agent_text_chunk events
- **THEN** the WebUI receives and accumulates the text chunks into a buffer, rendering them as streaming text in real-time

#### Scenario: Plan round start event received
- **WHEN** a `plan_round_start` event is received via SSE
- **THEN** the event is dispatched to the global event emitter for SessionTimeline to consume

### Requirement: Frontend agentStatus uses issueNumber field for matching

The frontend SSE event handlers and agent status detection SHALL use `issueNumber` (number) instead of `issueId` (UUID) for matching. Agent status endpoints return both `issueId` (UUID) and `issueNumber` (number). Frontend SHALL compare `agentStatus.issueNumber === issueNumber` for running detection and filter SSE events by `detail.issueId === String(issueNumber)`.

#### Scenario: Agent running detection works correctly
- **WHEN** agent is running on issue #5
- **THEN** `agentStatus.issueNumber === 5` evaluates to `true`
- **AND** the hook resets streaming state and begins accumulating events

#### Scenario: SSE event filtering works correctly
- **WHEN** a `coder_text_chunk` SSE event arrives with `issueId: "5"` (after backend fix)
- **AND** the user is viewing issue number 5
- **THEN** the event passes the filter and is processed

#### Scenario: SSE event for different issue filtered out
- **WHEN** a `coder_text_chunk` SSE event arrives with `issueId: "2"`
- **AND** the user is viewing issue number 5
- **THEN** the event is filtered out

### Requirement: AgentSessionPanel replaced by SessionTimeline

The IssueDetailPage SHALL replace the AgentSessionPanel component with a SessionTimeline component that provides round-based conversation display. The SessionTimeline SHALL load historical data from the workflow_log API and append live SSE events.

#### Scenario: Agent is running on this issue
- **WHEN** the user views an issue detail page and an agent is actively running
- **THEN** the SessionTimeline displays: pipeline status timeline, round-based conversation (collapsible), streaming agent text with typing cursor, and tool call timeline with expandable details

#### Scenario: Agent has completed, viewing history
- **WHEN** the user views an issue detail page and the agent has previously run
- **THEN** the SessionTimeline loads historical data from `GET /api/issues/:number/logs` and reconstructs the full round-based conversation

#### Scenario: Agent is mid-run when page opens
- **WHEN** the user navigates to issue detail page while the current agent run is still in progress
- **THEN** historical data from workflow_log is loaded first, then live SSE events are appended without duplication

### Requirement: Historical and live events merged without duplicates

When the SessionTimeline loads, it SHALL first fetch historical data from workflow_log API, then append live SSE events. Events SHALL be deduplicated using the following strategy:
- **Tool calls**: Use `Map<toolCallId, ToolCallEntry>` to merge started and completed states
- **Text chunks**: No deduplication needed (incremental accumulation)
- **Rounds**: Distinguish by `roundIndex`
- **Cross-run detection**: Detect new run when `plan_round_start` has `roundIndex === 0`

The complex "timestamp proximity and content overlap" strategy is NOT required.

#### Scenario: User opens page after agent run completes
- **WHEN** user navigates to issue detail page after a completed agent run and new live events begin for a subsequent run
- **THEN** historical messages from the previous run are loaded from workflow_log API, and live SSE events from the new run are appended without duplicating data

#### Scenario: Tool call merged from started and completed records
- **WHEN** workflow_log contains both `tool_call` (started) and `tool_call_update` (completed) for the same toolCallId
- **THEN** SessionTimeline displays a single ToolCallEntry with `state: 'completed'` and both `rawInput` and `rawOutput`
- **AND** duplicate tool calls from SSE are filtered by checking `toolCallMap.has(detail.toolCallId)`

### Requirement: Frontend uses RAF throttling for plan_session_update events

The SessionTimeline SHALL implement requestAnimationFrame-based throttling for `plan_session_update` events to prevent UI lockup during rapid streaming (1000+ events in Plan stage). Events SHALL be buffered in a ref and flushed every 100ms using `requestAnimationFrame`.

#### Scenario: Rapid plan_session_update events during Plan stage
- **WHEN** 1000+ `plan_session_update` events arrive within 5 seconds during Plan stage
- **THEN** the UI updates in batches (every 100ms) instead of per-event
- **AND** no frame drops occur during the streaming session

### Requirement: Semantic tool parts

The dedicated session page SHALL render normalized tool parts as semantic assistant conversation parts rather than raw event-log rows. Tool summaries SHALL be readable by default, with raw input/output available only through explicit disclosure.

#### Scenario: Context gathering is grouped

- **WHEN** adjacent context tools such as read, grep, glob, list, search, or memory reads appear in an assistant turn
- **THEN** the page renders a compact context group such as `Gathered context · 4 reads, 1 search`
- **AND** the group expands to show individual tool details and raw data

#### Scenario: Bash tools are summarized

- **WHEN** a bash or shell tool part is rendered
- **THEN** the default view shows a human title, command, status, duration where available, and concise output preview
- **AND** full output and raw payload are available through explicit disclosure

#### Scenario: File-changing tools show file summaries

- **WHEN** edit, write, or apply_patch tools change files
- **THEN** the default view shows changed file count, operation, path, and additions/deletions where available
- **AND** raw diff, patch, input, or output details are collapsed by default but expandable

#### Scenario: Unknown tools have useful fallback display

- **WHEN** a tool cannot be fully normalized
- **THEN** the visible title uses the best available display title, title, target, source name, or event label before falling back to `unknown`
- **AND** raw data remains available for debugging

### Requirement: Readable Mohist coder transcript

The dedicated session page SHALL read top-to-bottom as a Mohist prompt followed by a Coder response and resulting output. It SHALL resemble an opencode-style read-only conversation transcript more than a workflow dashboard or event log.

#### Scenario: Conversation speakers are clear

- **WHEN** a user reads the page from top to bottom
- **THEN** Mohist prompt cards are visibly distinct from Coder response parts
- **AND** each assistant response can include text, collapsed reasoning, semantic tools, errors, and file-change output in order

#### Scenario: Reasoning is collapsed by default

- **WHEN** reasoning or thought content exists
- **THEN** it is available behind a collapsed or summarized disclosure
- **AND** it does not dominate the primary transcript reading flow

#### Scenario: File changes appear as transcript output

- **WHEN** a turn or session includes file-changing tool output
- **THEN** touched paths and additions/deletions are visible in a compact transcript output section
- **AND** this output remains part of the conversation rather than a separate dashboard

#### Scenario: The page stays read-only

- **WHEN** the session page is rendered
- **THEN** it does not show a composer, continue-conversation input, stop control, steering control, or stage-control dashboard as part of this issue

### Requirement: Session page reads as a Mohist-to-Coder transcript

The coder session page SHALL present the session as a read-only Mohist-to-Coder transcript instead of an event log, workflow dashboard, or raw tool viewer.

#### Scenario: Prompt-led turns anchor the transcript

- **WHEN** the session page renders a transcript with one or more Mohist prompts
- **THEN** each Mohist prompt appears as the visible turn boundary
- **AND** assistant output is rendered beneath that prompt as ordered assistant parts

#### Scenario: Internal transcript noise stays out of the primary view

- **WHEN** a transcript includes internal tools, placeholders, or raw payload-first records
- **THEN** the primary transcript hides `todowrite`, stale `unknown` placeholders, and duplicate lifecycle fragments by default
- **AND** raw payloads are only shown in secondary expandable details when needed

#### Scenario: File-changing output belongs to the assistant turn

- **WHEN** the assistant applies patches or edits files during a turn
- **THEN** the turn shows compact file-change summaries and expandable diff details as part of that turn
- **AND** changed files do not appear only as detached workflow cards or summaries

### Requirement: Session page renders readable Mohist/Coder transcript

The dedicated session page SHALL render the session as a read-only Mohist-to-Coder transcript where prompt, reasoning, tools, and resulting output are understandable without reading raw event payloads.

#### Scenario: Prompt -> thinking -> action -> result reads as one flow

- **WHEN** a session turn contains prompt text, reasoning parts, assistant text, and tool activity
- **THEN** the page presents them in a readable conversation flow instead of as detached event-log sections
- **AND** reasoning remains collapsed by default so it does not dominate the first screen

#### Scenario: Tool identity remains readable

- **WHEN** a tool cannot be fully normalized into a special renderer
- **THEN** the visible tool title falls back to the best available raw tool name
- **AND** the subtitle uses high-signal fields such as `description`, `query`, `url`, `filePath`, or `path` when available

### Requirement: Context gathering is grouped without hiding detail

Adjacent context-gathering tools SHALL be grouped into compact transcript summaries while preserving full per-tool detail and failure visibility on expansion.

#### Scenario: Context group shows read/search counts

- **WHEN** adjacent read, glob, grep, list, membrowse, memread, or memsearch tools appear within one turn
- **THEN** the page renders a grouped summary such as `Gathering context · 3 reads · 2 searches`
- **AND** expanding the group reveals each individual tool and its raw details

#### Scenario: Failed context tools remain visible

- **WHEN** a grouped context tool fails
- **THEN** the group summary indicates that failure
- **AND** the failed tool remains visible in the expanded group

### Requirement: File-changing tools show diff-first results

File-changing transcript tools SHALL show user-readable change results before raw patch payloads.

#### Scenario: File-changing tool renders summary first

- **WHEN** apply_patch, edit, or write changes or intends to change files
- **THEN** the primary view shows file paths, operations, and additions/deletions or best-effort before/after diff information where available
- **AND** raw patch, input, and output remain available through explicit disclosure

### Requirement: Transcript metadata and controls stay embedded in the reading surface

Model, duration, copy actions, turn counts, and session state SHALL be surfaced within the transcript page itself rather than through a separate control dashboard.

#### Scenario: Copy and metadata stay local to transcript

- **WHEN** a user reads the transcript
- **THEN** assistant replies can be copied directly from the transcript surface
- **AND** model, duration or running/finalizing state, and turn count are visible in the page header or local transcript metadata

### Requirement: Live scrolling respects reader position

Live transcript updates SHALL use follow-mode behavior so users can inspect earlier content without being forced back to the bottom.

#### Scenario: Reader away from bottom is not interrupted

- **WHEN** new text, tool updates, recovery updates, or completion events arrive while the user is not near the bottom
- **THEN** the page does not force-scroll
- **AND** a jump-to-bottom affordance appears and restores follow mode when clicked

#### Scenario: Reader near bottom follows the stream

- **WHEN** the reader is already near the bottom during a live session
- **THEN** new transcript updates continue following the stream automatically

### Requirement: Issue detail session surfaces consume summary session payloads

The issue detail session list and compact session summary UI SHALL consume a summary-specific session contract instead of depending on full `workflowLogs` or transcript payloads from the list endpoint.

#### Scenario: Session list renders from summary metadata only

- **WHEN** the issue detail page renders the sessions list
- **THEN** list-oriented components use the summary payload shape returned by `GET /api/issues/:number/coder-sessions`
- **AND** they do not read `workflowLogs` from the list response

#### Scenario: Expensive derived counts are removed from the list surface

- **WHEN** the list response no longer includes workflow logs
- **THEN** `filesChanged` and `toolCalls` are removed or replaced by a lightweight non-log-backed presentation
- **AND** the session list and summary detail render without type errors

#### Scenario: Session page still loads full detail on demand

- **WHEN** a user opens a specific session page or drill-down view
- **THEN** that view still loads full transcript and log detail through the dedicated single-session endpoint

### Requirement: Issue-scoped session list queries reuse recent data briefly

The frontend query layer SHALL cache issue-specific coder session list results for a short stale window so brief navigation away from and back to the same issue does not immediately refetch the list.

#### Scenario: Recent list data is reused within the stale window

- **WHEN** a user leaves and returns to the same issue within about 30 seconds
- **THEN** the session list query reuses cached data for that issue
- **AND** the page does not immediately trigger a fresh list request on remount

#### Scenario: Cache keys remain issue-specific

- **WHEN** coder session lists are cached in the frontend
- **THEN** the cache key remains scoped to the issue identifier
- **AND** cached data from one issue is not shown for another issue

### Requirement: Session transcript preserves readable inline assistant flow

The dedicated session page SHALL present prompt, reasoning, assistant text, tools, and results as one readable Mohist-to-Coder flow instead of detached transcript fragments.

#### Scenario: Thinking remains inline with assistant output

- **WHEN** a turn contains alternating reasoning and assistant text chunks
- **THEN** the visible transcript preserves that interleaving instead of rendering all thinking as one detached block at the top
- **AND** refreshing after a live run does not materially change that visible order

### Requirement: File-changing transcript tools render diff-first semantic content

The session transcript UI SHALL render `apply_patch`, `edit`, and `write` tools as diff-first semantic content rather than raw JSON-first payloads.

#### Scenario: Edit-like tools show file changes first

- **WHEN** a file-changing tool produces normalized changed-file metadata and diff content
- **THEN** the primary transcript body shows changed files and readable diff content
- **AND** raw input and output remain available through secondary disclosure for audit/debugging

### Requirement: Semantic tool parts use a registry-based display model

Transcript tool rendering SHALL use a registry-based display contract and shared transcript parsing helpers so known tool families define human-readable title, subtitle, badges, display type, and file-change parsing without duplicating semantic logic across legacy and dedicated session transcript components.

#### Scenario: Known tools render semantic content

- **WHEN** bash, read, grep, glob, webfetch, question, task, skill, apply_patch, edit, or write tools appear in the transcript
- **THEN** legacy and registry-based transcript surfaces render human-readable headers and type-specific content using the same shared label, argument badge, display type, and patch/file-change parsing rules

#### Scenario: Running tools are visually distinct

- **WHEN** a tool is still running
- **THEN** the transcript shows a distinct animated running state rather than a static pending marker

#### Scenario: Tool display rules have one source of truth

- **WHEN** a developer adds or changes a transcript tool display rule
- **THEN** the change is made in `transcript-tool-utils` or `tool-registry`
- **AND** the legacy `ToolCallCard` path does not require a second copy of parsing logic for labels, arguments, display type, or patch operations

### Requirement: Transcript display summaries stay accurate for grouped and truncated content

Transcript presentation helpers SHALL keep summaries consistent with the rendered content, including grouped context tools, truncated search results, and legacy tool cards that consume shared transcript parsing helpers.

#### Scenario: Search ellipsis appears only when results were truncated

- **WHEN** a search content block renders all available results without truncation
- **THEN** no trailing ellipsis is shown
- **AND** an ellipsis is shown only when additional undisplayed results exist

#### Scenario: Grouped context tools still contribute changed-file summaries

- **WHEN** file-changing tools are nested inside a grouped context section
- **THEN** the turn-level changed-files summary includes those files
- **AND** a single context tool is rendered directly instead of being wrapped in a one-item group

#### Scenario: Shared transcript subtitle helpers are reused consistently

- **WHEN** a transcript tool needs a fallback subtitle
- **THEN** the transcript UI uses the shared subtitle helper instead of duplicating extraction logic

#### Scenario: Legacy and registry paths share file-change parsing

- **WHEN** `apply_patch`, `edit`, or `write` tools render changed-file summaries in either the legacy session view or the registry-based transcript view
- **THEN** both paths use shared patch/edit parsing helpers
- **AND** the visible file count, operation, path, and additions/deletions semantics remain consistent

### Requirement: Session transcript renders semantic tool rows

The session transcript page SHALL render tool calls using the most specific semantic title and summary available instead of a generic tool-family label.

#### Scenario: Skill tool shows loaded skill name

- **WHEN** a completed `skill` tool call has a loaded skill name in normalized title, input, or metadata
- **THEN** the transcript row shows that skill name, such as `Loaded skill: software-design`, instead of only `skill` or `unknown`

#### Scenario: Context group preserves child tool targets

- **WHEN** context-gathering tools are grouped in the transcript
- **THEN** the collapsed group may show an aggregate summary
- **AND** expanding the group shows per-tool semantic targets for `read`, `list`, `glob`, `grep`, `search`, and `search_files`

#### Scenario: Execution and delegation tools show semantic summaries

- **WHEN** the transcript renders `bash`, `shell`, `task`, `question`, `webfetch`, `websearch`, `todowrite`, or `todo`
- **THEN** each row shows semantic summaries such as command, cwd, exit code, subagent description, URL, query, or todo progress before any raw JSON fallback

### Requirement: Mutation tools render reviewable change content

Transcript rows for file-changing tools SHALL expose reviewable file-level changes as the primary expanded view.

#### Scenario: apply_patch renders per-file diffs

- **WHEN** an `apply_patch` tool call includes `patchText` or normalized patch metadata
- **THEN** the expanded tool view shows affected files with operation type and additions/deletions when available
- **AND** each file entry exposes an expandable diff body

#### Scenario: edit and write render semantic change views

- **WHEN** an `edit` or `write` tool call includes file target and before/after or diff metadata
- **THEN** the expanded tool view shows the target file plus a diff or written-content view
- **AND** raw JSON is shown only as a fallback when no semantic change representation can be derived

### Requirement: Prompt metadata avoids duplicate output target lines

The prompt card SHALL display one canonical output-target line when prompt subtitle and output-path metadata describe the same location.

#### Scenario: Duplicate output target is collapsed

- **WHEN** prompt summary metadata contains both `outputPath` and a subtitle equivalent to `Output: <same path>`
- **THEN** the transcript page shows that output target once
- **AND** no duplicate output-path line is rendered in the prompt block
