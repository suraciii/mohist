### Requirement: Transcript renders as a flat single-column timeline

The session transcript SHALL render all content — turn dividers, user prompts, assistant text, reasoning, and tool rows — in a single vertical column that shares one left margin and fills the available content-column width. The transcript SHALL NOT apply width-capping or centering (such as a `max-width` that narrows assistant text, prompts, or tool rows into a centered band) to any of this content, so that the reading line travels straight down the column. Assistant text SHALL render as borderless markdown filling the column, not as a width-limited chat bubble.

#### Scenario: Assistant text fills the column width
- **WHEN** a turn contains assistant text
- **THEN** the assistant text SHALL render full-width within the transcript column with no `max-width` cap narrowing it into a centered bubble
- **AND** the assistant text SHALL NOT be wrapped in a chat-bubble background or border

#### Scenario: Tool rows align to the single left margin
- **WHEN** a turn contains one or more tool rows
- **THEN** every tool row SHALL start at the same left edge as the assistant text and turn divider
- **AND** SHALL NOT be offset or indented into a narrower centered band

#### Scenario: User prompt is not a right-aligned bubble
- **WHEN** a turn's user prompt renders
- **THEN** the prompt SHALL render aligned to the shared left margin
- **AND** SHALL NOT be right-aligned or wrapped in a rounded chat-bubble container

### Requirement: Each turn has a full-width divider bar

Each turn SHALL be preceded by a full-width divider bar that spans the transcript column. The divider bar SHALL display the turn's ordinal number, the prompt type (for example Initial Task, Task, Follow-up, Retry, Recovery), and the turn's start time. For a turn that has completed, the divider bar SHALL additionally display the turn's elapsed duration, derived from the turn's start and completion timestamps.

#### Scenario: Completed turn divider shows number, type, time, and duration
- **WHEN** a completed turn renders
- **THEN** the divider bar SHALL span the full column width
- **AND** SHALL show the turn ordinal, the prompt type label, the start time, and the elapsed duration

#### Scenario: Running turn divider omits a finalized duration
- **WHEN** a turn is still running (no completion timestamp) renders
- **THEN** the divider bar SHALL show the turn ordinal, prompt type, and start time
- **AND** SHALL NOT display a finalized duration value

### Requirement: User prompt renders as a collapsed full-width block

The user prompt SHALL render as a full-width block carrying user-input visual semantics (blockquote-style), not as a right-aligned rounded chat bubble. A long prompt SHALL be collapsed by default, showing only a summary affordance, and SHALL provide an expand control to reveal the full prompt text. A short prompt MAY be shown without requiring expansion.

#### Scenario: Long prompt is collapsed by default and expandable
- **WHEN** a turn has a long user prompt
- **THEN** the prompt SHALL render collapsed by default as a full-width block (not a right-aligned bubble)
- **AND** SHALL expose an expand affordance that, when activated, reveals the full prompt text

#### Scenario: Prompt block fills the column
- **WHEN** any user prompt renders
- **THEN** the prompt block SHALL fill the transcript column width with no `max-width` cap that narrows it into a centered bubble

### Requirement: Transcript does not change session data or framing

The timeline rewrite SHALL be purely presentational. It SHALL NOT alter the transcript data model, the session event protocol, the event collection pipeline, or the session page header and framework shell. The same input turns SHALL produce the same turn/part projection; only their rendering changes.

#### Scenario: Data model is unchanged
- **WHEN** the timeline renders a session
- **THEN** the underlying transcript data model, event types, and collection pipeline SHALL remain unchanged from the pre-change behavior

#### Scenario: Session header and shell are unaffected
- **WHEN** the session page renders
- **THEN** the page header, usage summary, recovery bar, follow-up composer, and scroll behavior SHALL continue to render unchanged in their contracts
