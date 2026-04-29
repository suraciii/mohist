## ADDED Requirements

### Requirement: SessionPage route renders full-page session detail
The system SHALL provide a `SessionPage` component at route `/issue/:number/session/:sessionId` that renders a full-width, immersive session detail view. The page SHALL extract `number` and `sessionId` from URL params, load the session data, and display rounds as vertically stacked conversation cards.

#### Scenario: Navigate to session page from issue detail
- **WHEN** the user clicks a session header in the SessionList on the issue detail page
- **THEN** the browser navigates to `/issue/:number/session/:sessionId`
- **AND** the SessionPage component renders with the session's full timeline

#### Scenario: Direct URL access to session page
- **WHEN** the user navigates directly to `/issue/86/session/abc-123`
- **THEN** the SessionPage loads the session data and renders the full conversation timeline
- **AND** displays a 404 state if the session does not exist

### Requirement: SessionPage displays breadcrumb navigation
The SessionPage SHALL display a breadcrumb header at the top containing: a back arrow linking to `/issue/:number`, the issue number, and the issue title. The header SHALL also display session metadata: stage label, model name, and total duration.

#### Scenario: Breadcrumb links back to issue
- **WHEN** the user clicks the back arrow or issue number in the breadcrumb
- **THEN** the browser navigates to `/issue/:number`

#### Scenario: Session metadata in header
- **WHEN** the session page loads for a completed plan session
- **THEN** the header displays "Plan · model-name · 18m 58s" alongside the issue breadcrumb

#### Scenario: Running session shows live indicator
- **WHEN** the session is actively running
- **THEN** the header shows a pulsing blue dot with "Live" label
- **AND** the duration updates in real-time

### Requirement: SessionPage renders rounds as conversation cards
Each round SHALL render as a bordered card containing: a round header (label, timestamp, duration), agent text rendered as prose paragraphs, and tool calls rendered inline with tool-specific display logic. Agent text and tool calls SHALL be interleaved in the order they occurred within the round.

#### Scenario: Round with agent text and tool calls
- **WHEN** a round contains agent text followed by an edit tool call followed by more agent text
- **THEN** the round card displays: agent text paragraph → edit tool call with diff → agent text paragraph
- **AND** the visual flow reads as a conversation, not a flat list

#### Scenario: Round with only agent text
- **WHEN** a round contains only agent text and no tool calls
- **THEN** the round card displays the agent text as a prose block without any tool call section

### Requirement: Edit tool calls render inline diff view
Edit tool calls (toolName `edit` or `write`) SHALL render as an inline diff card showing: the file path as a header, a unified diff with added lines highlighted green and removed lines highlighted red, and a footer with status and duration. The diff SHALL be extracted from the tool call's `rawInput` field.

#### Scenario: Edit tool call with file modification
- **WHEN** an edit tool call has `rawInput` containing `oldString` and `newString`
- **THEN** the tool call renders as a diff card with the file path in the header
- **AND** lines from `oldString` are shown with red `-` prefix
- **AND** lines from `newString` are shown with green `+` prefix

#### Scenario: Edit tool call with file creation (write)
- **WHEN** a write tool call has `rawInput` containing `content` but no `oldString`
- **THEN** all content lines are shown with green `+` prefix (new file)

#### Scenario: Edit tool call still in progress
- **WHEN** an edit tool call has `state: 'started'`
- **THEN** the card shows the file path with a spinning indicator and "editing..." label

### Requirement: Bash tool calls render terminal output
Bash tool calls SHALL render as a terminal-style block showing: the command as a header (with `$` prefix), the output in a monospace pre block with a dark background, and a footer with exit status and duration. The output SHALL default to collapsed if longer than 10 lines, expandable on click.

#### Scenario: Bash with short output
- **WHEN** a bash tool call has output of 5 lines
- **THEN** all 5 lines are displayed in the terminal block without truncation

#### Scenario: Bash with long output auto-collapsed
- **WHEN** a bash tool call has output of 50 lines
- **THEN** the first 10 lines are shown with a "Show more (40 more lines)" toggle
- **AND** clicking the toggle expands to show all 50 lines

#### Scenario: Bash with error exit
- **WHEN** a bash tool call exits with non-zero status
- **THEN** the header shows a red error indicator
- **AND** the output block has a red left border

### Requirement: Low-value tool calls render as collapsed summaries
Tool calls for `read`, `glob`, `grep`, `todowrite`, `webfetch`, `memread`, `membrowse`, `memsearch` SHALL render as a single-line collapsed summary showing: tool icon, tool name, and a brief title derived from the input (file path for read, pattern for glob/grep, description for todowrite). Clicking SHALL expand to show full input/output.

#### Scenario: Read tool call summary
- **WHEN** a read tool call with `rawInput` containing `filePath: "src/components/App.tsx"` is rendered
- **THEN** a single line shows "read src/components/App.tsx" with a file icon
- **AND** clicking expands to show the full file content output

#### Scenario: Glob tool call summary
- **WHEN** a glob tool call with `rawInput` containing `pattern: "**/*.tsx"` is rendered
- **THEN** a single line shows "glob **/*.tsx" with a search icon
- **AND** clicking expands to show the matched file list

#### Scenario: Todowrite tool call summary
- **WHEN** a todowrite tool call is rendered
- **THEN** a single line shows "todowrite" with a task icon and the count of items

#### Scenario: Unknown tool call defaults to summary
- **WHEN** a tool call with an unrecognized toolName is rendered
- **THEN** it renders as a summary line with the tool name and duration
- **AND** clicking expands to show raw input/output

### Requirement: SessionPage supports live SSE streaming
When the session is actively running, SessionPage SHALL subscribe to the same SSE events as the inline view (`coder_text_chunk`, `coder_tool_call`, `plan_session_update`, `plan_round_start`) and append events to the current round in real-time. The page SHALL auto-scroll to the bottom as new content arrives.

#### Scenario: Streaming text into running session
- **WHEN** the user is viewing a running session page
- **AND** `coder_text_chunk` events arrive
- **THEN** new text appears at the bottom of the current round with a typing cursor animation
- **AND** the page auto-scrolls to keep the new content visible

#### Scenario: New tool call appears during streaming
- **WHEN** a `coder_tool_call` event arrives for a running session
- **THEN** the tool call appears inline in the current round at the appropriate position
- **AND** edit tool calls show their diff, bash shows terminal output, etc.

#### Scenario: New round starts during streaming
- **WHEN** a `plan_round_start` event arrives
- **THEN** a new round card appears at the bottom of the timeline
- **AND** subsequent text and tool call events accumulate in this new round
