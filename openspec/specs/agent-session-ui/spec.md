### Requirement: Readable Mohist coder transcript

The dedicated session page SHALL read top-to-bottom as a Mohist prompt followed by a Coder response and resulting output. It SHALL resemble an opencode-style conversation transcript more than a workflow dashboard or event log. The page SHALL always render a session header/breadcrumb above the transcript across every render branch, including the main transcript view when one or more turns exist, the empty state, and the loading/waiting state. The header SHALL display the session title, status badge, and turn count. For a session bound to a workflow run, the header SHALL additionally display the workflow stage and a link back to the owning issue. For a generic (non-workflow) `AgentSession`, the header SHALL link back to the owning Agent profile, or to the referenced issue when an issue context reference exists, and SHALL omit workflow-only fields rather than fabricate them. The recovery bar, when present, SHALL render as a sub-region of this header rather than as a standalone narrow bar, and SHALL remain visible (sticky) within the page scroll context while the transcript body scrolls so the Compact/Reset actions and context-health bar stay reachable at all times. During an active (running) session, the page MAY additionally present a followup composer at the bottom so the user can inject messages mid-run without canceling the agent. Compaction events SHALL also be surfaced in a compact summary atop the transcript rather than only inside expanded transcript rounds, so a user can see that context was compacted without expanding individual rounds. The header-above-transcript, recovery-bar, compaction-summary, followup-composer, timestamp, syntax-highlighting, and responsive behaviors SHALL apply uniformly to both workflow-bound and generic sessions.

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

#### Scenario: Active session shows followup composer

- **WHEN** the session page is rendered for a session in a running or active state
- **THEN** a followup composer (chat input with textarea and send affordance) SHALL be visible at the bottom of the transcript
- **AND** the composer SHALL NOT include a stop control, steering control, or stage-control dashboard

#### Scenario: Terminal session disables followup composer

- **WHEN** the session page is rendered for a session in a terminal state (completed, failed)
- **THEN** the followup composer SHALL be disabled or hidden
- **AND** the page SHALL NOT accept new followup input

#### Scenario: Session header always renders above the transcript

- **WHEN** the session page renders the main transcript view with one or more turns
- **THEN** the session header/breadcrumb SHALL render above the transcript
- **AND** the header SHALL show the session title, status badge, and turn count
- **AND** for a workflow-bound session the header SHALL additionally show the workflow stage and a link back to the owning issue
- **AND** the recovery bar, when present, SHALL render within the header region rather than as a standalone narrow bar
- **AND** the rendered header SHALL match the header shown in the empty and waiting states

#### Scenario: Generic session header links back to the owning agent

- **WHEN** the session page renders a generic (non-workflow) AgentSession
- **THEN** the header SHALL link back to the owning Agent profile
- **OR** to the referenced issue when an issue context reference exists
- **AND** the header SHALL omit workflow-only fields (workflow stage, owning issue link) rather than fabricating them

#### Scenario: Recovery bar stays visible while the transcript scrolls

- **WHEN** the recovery bar is present and the transcript body is scrolled
- **THEN** the recovery bar SHALL remain visible within the page scroll context (sticky)
- **AND** the Compact/Reset actions and context-health bar SHALL remain reachable without scrolling back to the top

#### Scenario: Compaction events surface in a compact summary atop the transcript

- **WHEN** the session has one or more recorded compaction events
- **THEN** the transcript SHALL surface a compact summary of those compaction events atop the transcript
- **AND** the summary SHALL be visible without expanding an individual transcript round

### Requirement: Turn-level timestamps in transcript

The transcript layout SHALL display a timestamp for each turn. The timestamp SHALL reflect the turn's time and SHALL be visible in or alongside the turn so the user can see when each turn occurred while reading top to bottom.

#### Scenario: Each turn shows a timestamp

- **WHEN** the transcript layout renders a turn that has a timestamp
- **THEN** that turn SHALL display its timestamp
- **AND** the timestamp SHALL be visible in the normal reading flow of the transcript

### Requirement: Syntax-highlighted code blocks in transcript

The transcript SHALL render fenced code blocks with syntax highlighting, layered on top of the existing markdown rendering pipeline. Highlighting SHALL apply to code blocks across assistant responses in the transcript and SHALL NOT alter non-code content.

#### Scenario: Fenced code blocks are highlighted

- **WHEN** an assistant response in the transcript contains a fenced code block
- **THEN** the rendered code block SHALL be syntax-highlighted
- **AND** non-code markdown content SHALL continue to render as before

### Requirement: Responsive session transcript on narrow viewports

The session transcript page SHALL remain usable on narrow viewports down to 320px width. The session header SHALL NOT wrap into a broken or overlapping layout on narrow viewports, prompt and assistant cards SHALL NOT deform, and the page SHALL NOT produce horizontal overflow across the 320-430px viewport range.

#### Scenario: No horizontal overflow on a narrow viewport

- **WHEN** the transcript page is rendered in a viewport between 320px and 430px wide
- **THEN** the page SHALL NOT produce horizontal overflow
- **AND** the session header SHALL remain legible without broken wrapping or overlapping elements
- **AND** prompt and assistant cards SHALL retain their intended layout without deformation
