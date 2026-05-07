## MODIFIED Requirements

### Requirement: Read-only session transcript
The dedicated session detail page SHALL render the coder session as a read-only conversation transcript. The main timeline SHALL be organized by Mohist prompt turns and coder assistant parts rather than workflow summaries, task dashboards, or raw ACP JSON.

#### Scenario: Session page shows Mohist prompt
- **WHEN** the user opens `/issue/:number/session/:sessionId`
- **THEN** each turn displays a Mohist prompt card with prompt kind and sent time
- **AND** long prompts are collapsed by default but can be expanded to full text
- **AND** the full prompt can be copied

#### Scenario: Assistant text renders as markdown
- **WHEN** a coder agent text part is displayed
- **THEN** headings, lists, inline code, and fenced code blocks render as readable markdown
- **AND** code blocks preserve formatting and allow horizontal scrolling

#### Scenario: Reasoning is auditable but not dominant
- **WHEN** a reasoning or thinking part exists
- **THEN** it is collapsed by default with size and timestamp summary
- **AND** the user can expand it for audit

#### Scenario: Page remains read-only
- **WHEN** the session detail page renders
- **THEN** it does not render an input box, composer, continue-conversation control, or disabled fake input

### Requirement: Tool parts progressive disclosure
Tool calls SHALL render as assistant parts inside the corresponding conversation turn. Every tool part SHALL show status and target summary by default, with expandable input/output details and a generic fallback for unknown tools.

#### Scenario: Bash tool part
- **WHEN** a bash tool part is rendered
- **THEN** it shows the command and terminal-style output
- **AND** ANSI output is safely handled or stripped

#### Scenario: Edit-like tool part
- **WHEN** an edit, write, or apply_patch tool part is rendered
- **THEN** it shows the target path and a diff-like summary when possible
- **AND** raw input/output remain expandable when a diff summary cannot be derived

#### Scenario: Context-gathering tool part
- **WHEN** a read, grep, or glob tool part is rendered
- **THEN** it appears as a compact row with target or pattern summary
- **AND** full input/output details remain expandable

#### Scenario: Unknown tool part
- **WHEN** a tool name has no specialized renderer
- **THEN** the UI renders a generic tool part with name, status, target/title, expandable input/output, and error if any

### Requirement: Session transcript acceptance
The completed session transcript experience SHALL satisfy persistence, replay, rendering, and read-only behavior end to end.

#### Scenario: Historical completed session
- **WHEN** the user opens a completed coder session
- **THEN** Mohist prompts, coder markdown, reasoning, tool parts, and errors/recovery events display from persisted data

#### Scenario: Legacy incomplete session
- **WHEN** an old session lacks persisted Mohist prompts
- **THEN** the page clearly marks the transcript as incomplete and still displays available coder output and tool parts

#### Scenario: Workflow context does not replace transcript
- **WHEN** workflow, task, check, or diff context is available
- **THEN** it may appear in header or optional context areas
- **AND** it does not replace the main conversation transcript
