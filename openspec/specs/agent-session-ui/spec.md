### Requirement: Readable Mohist coder transcript

The dedicated session page SHALL read top-to-bottom as a Mohist prompt followed by a Coder response and resulting output. It SHALL resemble an opencode-style conversation transcript more than a workflow dashboard or event log. During an active (running) session, the page MAY additionally present a followup composer at the bottom so the user can inject messages mid-run without canceling the agent.

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