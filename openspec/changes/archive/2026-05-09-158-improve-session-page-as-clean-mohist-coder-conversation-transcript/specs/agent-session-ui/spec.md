## ADDED Requirements

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
