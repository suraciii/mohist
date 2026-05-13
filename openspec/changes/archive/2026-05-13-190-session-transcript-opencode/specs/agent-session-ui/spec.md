## MODIFIED Requirements

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

### Requirement: Semantic tool parts use a registry-based display model

Transcript tool rendering SHALL use a registry-based display contract so known tool families can define human-readable title, subtitle, badges, and content rendering without duplicating logic across components.

#### Scenario: Known tools render semantic content

- **WHEN** bash, read, grep, glob, webfetch, question, task, skill, apply_patch, edit, or write tools appear in the transcript
- **THEN** the page renders human-readable headers and type-specific content instead of showing raw JSON as the default view

#### Scenario: Running tools are visually distinct

- **WHEN** a tool is still running
- **THEN** the transcript shows a distinct animated running state rather than a static pending marker

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
