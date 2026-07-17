### Requirement: Each tool call renders as a single default-collapsed row

Each tool call SHALL render as a single row by default. The collapsed row SHALL display a status symbol reflecting the tool's state, a verb-led title describing the action performed (for example read, ran, edited, searched), the key parameters needed to recognize what the call targeted, and the elapsed duration derived from the tool's start and completion timestamps. Typed detail content — command/terminal output, inline diffs, and result summaries — SHALL remain hidden until the user expands the row.

#### Scenario: Collapsed row shows status, title, parameters, and duration
- **WHEN** a completed tool call renders
- **THEN** the row SHALL show a status symbol, a verb-led title, key parameters, and the elapsed duration on a single line
- **AND** SHALL NOT show the tool's full input/output detail until the row is expanded

#### Scenario: Expanding a row reveals typed detail content
- **WHEN** a user activates a collapsed tool row's expand control
- **THEN** the row SHALL reveal its typed detail content appropriate to the tool kind (terminal/command output for shell tools, an inline diff for edit tools, or a result summary otherwise)

#### Scenario: Tool row lacks a chat-bubble or width cap
- **WHEN** a tool row renders
- **THEN** the row SHALL fill the transcript column width and SHALL NOT be narrowed by a `max-width` cap into a centered card

### Requirement: Edit-type tool rows show changed file and line statistics inline

A tool call that modifies one or more files SHALL display, inline on its collapsed row, the changed file path and the line-change statistics (`+N` additions and `−M` deletions). This information SHALL be visible without expanding the row. When the change touches a single file, the file path SHALL be shown; when it touches multiple files, the row SHALL summarize the file count.

#### Scenario: Single-file edit shows file and line stats inline
- **WHEN** a completed edit tool call modifies exactly one file with known additions and deletions
- **THEN** the collapsed row SHALL display the changed file path together with `+N` and `−M` line statistics inline
- **AND** SHALL NOT require expansion to see the file path or line counts

#### Scenario: Multi-file edit summarizes count inline
- **WHEN** a completed edit tool call modifies more than one file
- **THEN** the collapsed row SHALL summarize the number of changed files inline
- **AND** the individual file list SHALL be available upon expansion

### Requirement: Failed tool calls render the whole row in a danger style

A tool call whose state is failed SHALL render its entire row with a red/danger visual treatment, so that the failure is recognizable while scanning the timeline without expanding the row. A non-failed tool call SHALL NOT receive the whole-row danger treatment.

#### Scenario: Failed tool row is visually red
- **WHEN** a tool call with state `failed` renders
- **THEN** the entire row SHALL be rendered with danger (red) styling
- **AND** the row SHALL be distinguishable as failed without requiring expansion

#### Scenario: Successful tool row is not red
- **WHEN** a tool call with state `completed` renders
- **THEN** the row SHALL NOT receive whole-row danger styling

### Requirement: Running tool calls render a one-line in-progress state

A tool call that is pending or running SHALL render as a single-line in-progress state within the same row structure, indicating the action in flight (for example "Editing WorkflowDefinition.cs…"). A running row SHALL NOT display a live-ticking, wall-clock-updating duration; only finalized durations are shown on completed rows.

#### Scenario: Running tool shows an in-progress line
- **WHEN** a tool call with state `running` or `pending` renders
- **THEN** the row SHALL render a single-line in-progress state describing the action in flight
- **AND** SHALL NOT display a continuously updating elapsed timer

### Requirement: Each tool row exposes a stable locatable semantic structure

Every tool row SHALL expose a stable, addressable structure anchored to its tool call identity and carrying its semantic state, so that later enhancements (such as live-activity indicators and navigation anchors) can target individual calls. Each row SHALL carry a stable identifier derived from the tool call id and a semantic attribute expressing its current state.

#### Scenario: Tool row carries a stable identity and state
- **WHEN** a tool row renders
- **THEN** the row SHALL expose a stable identifier tied to its tool call id (for example via a `data-*` attribute)
- **AND** SHALL expose its current tool state as a semantic attribute
- **AND** the identifier SHALL remain stable across re-renders as long as the underlying tool call id is unchanged

#### Scenario: Tool row identity survives streaming updates
- **WHEN** the transcript updates while a session is streaming and a tool call transitions from running to completed
- **THEN** the row's stable identifier SHALL remain unchanged across the state transition
