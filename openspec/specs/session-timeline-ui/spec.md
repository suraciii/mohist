# OpenSpec Capability: session-timeline-ui

### Requirement: SessionTimeline component renders rounds

The IssueDetailPage SHALL include a SessionTimeline component that renders agent session activity grouped by rounds. Each round SHALL be a collapsible section showing: round label (e.g., "Proposal", "Specs", "Design"), timestamp, and agent output summary.

#### Scenario: Plan stage with 5 rounds
- **WHEN** the user views an issue that completed the Plan stage (5 rounds: proposal, specs, design, tasks, self-review)
- **THEN** SessionTimeline renders 5 collapsible round sections, each labeled with the round type and timestamp

#### Scenario: Round expanded to show conversation
- **WHEN** the user clicks on a round section
- **THEN** the section expands to show the full conversation: user prompt, agent text output, and tool call entries with status icons

### Requirement: SessionTimeline loads history from workflow_log API

When the page loads for a non-draft issue, SessionTimeline SHALL fetch historical data from `GET /api/issues/:number/logs` and reconstruct the round-based conversation structure by splitting on `user_message_chunk` events. This replaces the broken `GET /api/issues/:number/agent-session` source which returns empty results for pipeline runs.

#### Scenario: Page loads after plan stage completes
- **WHEN** the user navigates to an issue detail page after the plan stage has completed
- **THEN** SessionTimeline fetches all workflow_log entries and reconstructs rounds by grouping events between consecutive `user_message_chunk` entries

#### Scenario: No workflow_log entries exist
- **WHEN** the user views a draft issue with no agent activity
- **THEN** SessionTimeline shows "No agent activity yet" placeholder

### Requirement: SessionTimeline appends live SSE events

When an agent is actively running on the current issue, SessionTimeline SHALL subscribe to `plan_session_update` and `plan_round_start` SSE events and append them to the current round in real-time. For Build stage, it SHALL also subscribe to `coder_text_chunk` and `coder_tool_call`.

#### Scenario: Agent starts new round while user is viewing
- **WHEN** the user is viewing the issue page and a new plan round starts
- **THEN** a new round section appears with the round label and begins accumulating agent output in real-time

#### Scenario: Agent text chunks stream into current round
- **WHEN** `plan_session_update` events with `sessionUpdate: 'agent_message_chunk'` arrive
- **THEN** the agent text in the current round updates with a typing cursor animation, using requestAnimationFrame-batched rendering

#### Scenario: Build stage coder events stream
- **WHEN** `coder_text_chunk` events arrive during Build stage (executionId starts with `build-`)
- **THEN** the text is appended to the current task's round section

### Requirement: Tool calls in timeline show expandable details

Each tool call entry in a round SHALL display the tool name, status icon, and duration. Completed tool calls SHALL be expandable to show input arguments and output result.

#### Scenario: Tool call with read input and directory output
- **WHEN** a tool_call_update with kind 'read' and completed status is rendered
- **THEN** the entry shows "read" with a green checkmark and a file path preview
- **AND** clicking expands to show the full file path and directory listing output

### Requirement: Pipeline status timeline

The IssueDetailPage SHALL show a pipeline status timeline above SessionTimeline, displaying key events: pipeline start, each round completion with artifact produced, gate status, and any errors.

#### Scenario: Pipeline in plan stage with gate awaiting
- **WHEN** the plan stage completes and is awaiting approval
- **THEN** the timeline shows: "Pipeline started" → "✓ Proposal" → "✓ Specs" → "✓ Design" → "✓ Tasks" → "✓ Self-review" → "⏸ Awaiting approval"

### Requirement: Coder session rounds in Build stage

During the Build stage, SessionTimeline SHALL render coder sessions as rounds labeled by task ID and description. Each coder round SHALL show the coder's agent text and tool calls. Data comes from `coder_sessions` API (historical) and `coder_text_chunk`/`coder_tool_call` SSE events (live).

#### Scenario: Build stage with 3 completed tasks
- **WHEN** the user views an issue in build stage with 3 completed coder sessions
- **THEN** SessionTimeline shows 3 coder rounds labeled "[T-001] Task name", "[T-002] Task name", "[T-003] Task name" with completion status

#### Scenario: Coder round with tool call details
- **WHEN** a coder session round is expanded and coder_tool_call events included rawInput/rawOutput
- **THEN** each tool call shows its name, input args (formatted), and output result (truncated)

### Requirement: Frontend uses RAF throttling for high-frequency events

The `useSessionTimeline` hook SHALL implement requestAnimationFrame-based throttling for `plan_session_update` events to prevent UI lockup during rapid streaming. Events SHALL be buffered in a ref and flushed every 100ms using `requestAnimationFrame`, matching the existing pattern in `useAgentSession`.

#### Scenario: Rapid plan_session_update events during Plan stage
- **WHEN** 1000+ `plan_session_update` events arrive within 5 seconds during Plan stage
- **THEN** the UI updates in batches (every 100ms) instead of per-event
- **AND** no frame drops occur during the streaming session

### Requirement: Session transcript quality regression

Session timeline and session detail surfaces SHALL not expose raw stream fragments as the primary coder-session experience. Regression coverage SHALL prove active and historical sessions remain readable after event normalization, refresh, and live streaming.

#### Scenario: Completed tools render once after refresh

- **WHEN** a persisted session is replayed after refresh
- **THEN** completed tools appear once with stable name, title, status, and details
- **AND** pending/update fragments do not create orphan `unknown running...` entries

#### Scenario: Context and file output remain compact

- **WHEN** a session contains many context tools and file-changing tools
- **THEN** context gathering is grouped into compact summaries
- **AND** file changes are visible as compact transcript output

#### Scenario: Live and historical views agree

- **WHEN** a live session receives streamed text and tool updates and is later refetched from persisted data
- **THEN** the visible transcript order, tool grouping, and file-change summaries remain equivalent

#### Scenario: Raw debugging data remains accessible

- **WHEN** normalized transcript parts hide raw event detail by default
- **THEN** raw prompt, tool input, tool output, and relevant debugging data remain available through explicit disclosure or raw-log access

### Requirement: Session timeline uses opencode-like reading layout and live affordances

The session timeline SHALL use a centered reading column, sticky in-flow session header, subtle running indicators, and user-respecting scrolling behavior that matches an opencode-style transcript experience.

#### Scenario: Transcript layout prioritizes reading flow

- **WHEN** the session page renders on desktop or mobile
- **THEN** the transcript content is shown in a centered reading column rather than a dashboard-width layout
- **AND** the session title and running state remain visible via a sticky in-timeline header

#### Scenario: Running sessions show subtle progress

- **WHEN** the session is live or finalizing
- **THEN** the page shows an inline spinner and subtle progress treatment in the timeline header
- **AND** the UI does not rely on rapidly accumulating tool cards to communicate activity

#### Scenario: Auto-follow respects reader intent

- **WHEN** new transcript content arrives while the reader is bottom-locked
- **THEN** the timeline auto-follows the new content
- **BUT WHEN** the reader scrolls away, selects text, or scrolls inside a nested `data-scrollable` region
- **THEN** auto-follow pauses until the reader explicitly returns to the bottom
- **AND** a jump-to-bottom control appears only when it is needed

### Requirement: Session surfaces show shared transcripts without collapsing task progress

Session timeline and transcript surfaces SHALL display a shared Plan agent session as one coherent transcript containing separate Mohist prompt blocks for the artifact tasks that executed in that session. Issue task progress surfaces SHALL continue to show each Plan artifact task as an independent task row.

#### Scenario: Shared Plan transcript contains multiple prompt blocks
- **WHEN** the user opens the session surface for a Plan run whose artifact tasks used `agentSessionRef: "plan-artifacts"`
- **THEN** the session surface SHALL show one Plan transcript for the real shared session
- **AND** that transcript SHALL include separate Mohist prompt blocks for the individual artifact tasks that executed in it

#### Scenario: Task list remains independent
- **WHEN** the user views the issue detail task progress for the same Plan run
- **THEN** the task list SHALL still show separate rows for `proposal`, `specs`, `design`, `tasks`, and `self-review`
- **AND** task completion SHALL NOT be inferred from transcript completion

#### Scenario: Build and Check session display is unchanged by default
- **WHEN** Build or Check tasks do not explicitly configure `agentSessionRef`
- **THEN** their session/transcript display SHALL remain task-local as before this change

