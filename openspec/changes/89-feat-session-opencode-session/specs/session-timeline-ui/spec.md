## MODIFIED Requirements

### Requirement: SessionTimeline component renders rounds
The IssueDetailPage SHALL include a SessionTimeline component that renders agent session activity grouped by rounds. Each round SHALL be a collapsible section showing: round label (e.g., "Proposal", "Specs", "Design"), timestamp, and agent output summary. In the inline (issue detail page) context, each round SHALL be collapsed by default, showing only the round header. The full round content SHALL be available on the session detail page via the session header link.

#### Scenario: Plan stage with 5 rounds on issue detail page
- **WHEN** the user views an issue that completed the Plan stage (5 rounds: proposal, specs, design, tasks, self-review)
- **THEN** SessionTimeline renders 5 round sections with headers visible
- **AND** each round is collapsed by default showing only the label and timestamp

#### Scenario: Round expanded to show conversation
- **WHEN** the user clicks on a round section
- **THEN** the section expands to show the full conversation: user prompt, agent text output, and tool call entries with status icons

### Requirement: Tool calls in timeline show expandable details
Each tool call entry in a round SHALL display the tool name, status icon, and duration. Completed tool calls SHALL be expandable to show input arguments and output result. In the session page context, tool calls SHALL additionally render with tool-specific display: edit calls show inline diff, bash calls show terminal output, and read/glob/grep calls show collapsed summaries.

#### Scenario: Tool call with read input and directory output
- **WHEN** a tool_call_update with kind 'read' and completed status is rendered
- **THEN** the entry shows "read" with a green checkmark and a file path preview
- **AND** clicking expands to show the full file path and directory listing output

#### Scenario: Edit tool call in session page context
- **WHEN** an edit tool call is rendered on the session detail page
- **THEN** it renders as an inline diff card showing added/removed lines
- **AND** the file path is shown in the card header

### Requirement: Coder session rounds in Build stage
During the Build stage, SessionTimeline SHALL render coder sessions as rounds labeled by task ID and description. Each coder round SHALL show the coder's agent text and tool calls. Data comes from `coder_sessions` API (historical) and `coder_text_chunk`/`coder_tool_call` SSE events (live). On the issue detail page, each session in the SessionList SHALL be a clickable link navigating to `/issue/:number/session/:sessionId`.

#### Scenario: Build stage with 3 completed tasks
- **WHEN** the user views an issue in build stage with 3 completed coder sessions
- **THEN** SessionTimeline shows 3 coder rounds labeled "[T-001] Task name", "[T-002] Task name", "[T-003] Task name" with completion status

#### Scenario: Coder round with tool call details
- **WHEN** a coder session round is expanded and coder_tool_call events included rawInput/rawOutput
- **THEN** each tool call shows its name, input args (formatted), and output result (truncated)

### Requirement: SessionHeader navigates to session page
The `SessionHeader` component SHALL render as a `<Link>` element navigating to `/issue/:number/session/:sessionId` instead of an expand/collapse toggle button. The inline `SessionDetail` component SHALL render a summary-only view: file change count (derived from edit/write tool calls) and a count of key operations, without expanding the full timeline.

#### Scenario: Click session header to navigate
- **WHEN** the user clicks a session header in the SessionList
- **THEN** the browser navigates to `/issue/:number/session/:sessionId`
- **AND** no inline expansion occurs

#### Scenario: SessionList shows summary for each session
- **WHEN** the user views the SessionList on the issue detail page
- **THEN** each session shows its header (label, status, duration) as a clickable link
- **AND** below the header a single line summary shows "3 files changed · 5 tool calls" (or similar)
- **AND** no full timeline is rendered inline
