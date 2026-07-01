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

### Requirement: Session-level complete usage summary

The session page SHALL surface a complete session-level usage summary that makes every usage field the server transmits observable in one place: input tokens, output tokens, total tokens, cache-saved (`cachedReadTokens`) tokens, reasoning (`thoughtTokens`) tokens, cost, context-window tokens used, context-window size, context-usage percentage, and health status. The summary SHALL be visible on the session page without requiring the user to navigate to a separate dashboard or issue a request beyond the session metadata the page already loads. The summary SHALL replace the `SessionDetail` dead-stub region (which renders only a literal label and no session data) with a region that presents this substantive session usage, so the region earns its place on the page. When a given usage field is not applicable or unavailable for a session (for example `thoughtTokens` for a non-reasoning model, or `cachedReadTokens` when no cache hit occurred), the summary SHALL present that field gracefully rather than rendering a misleading value.

#### Scenario: All usage fields are visible in one summary

- **WHEN** a session page renders for a session whose server-provided usage includes all usage fields
- **THEN** the session page SHALL display a usage summary covering input, output, total, cache-saved, and thought tokens, cost, context-window used and size, context-usage percentage, and health status
- **AND** the summary SHALL be visible in a single observable region without navigating away

#### Scenario: Cache-saved tokens are surfaced

- **WHEN** a session accrued cache-saved (`cachedReadTokens`) tokens
- **THEN** the usage summary SHALL display the cache-saved token count
- **AND** the value SHALL NOT be silently dropped from the rendered UI

#### Scenario: Reasoning tokens are surfaced

- **WHEN** a session produced by a reasoning model accrued thought (`thoughtTokens`) tokens
- **THEN** the usage summary SHALL display the thought-token count
- **AND** the value SHALL NOT be silently dropped from the rendered UI

#### Scenario: Missing usage fields degrade gracefully

- **WHEN** a usage field is not applicable or unavailable for a session
- **THEN** the summary SHALL present that field gracefully by omission or an explicit not-applicable treatment
- **AND** the summary SHALL NOT render a misleading zero or placeholder value

#### Scenario: Session detail region renders real content instead of a stub

- **WHEN** the session page renders the session-detail region
- **THEN** the region SHALL display substantive session information (usage detail and/or session metadata)
- **AND** the region SHALL NOT render as a placeholder that displays only a literal label and no session data

### Requirement: Token detail in the session observability bar and header row

The session page observability bar / header row SHALL render the complete token detail, including the cache-saved (`cachedReadTokens`) and reasoning (`thoughtTokens`) token counts in addition to the input, output, and total tokens already shown. The cached and thought token counts SHALL be visible alongside the other token metrics in the observability bar so the full token明細 is observable at a glance, rather than being carried in the data model and rendered by zero components.

#### Scenario: Cached and thought tokens appear in the observability bar

- **WHEN** the session page renders the observability bar / header row for a session that accrued cache-saved or thought tokens
- **THEN** the bar SHALL display the cache-saved (`cachedReadTokens`) token count
- **AND** the bar SHALL display the reasoning (`thoughtTokens`) token count
- **AND** both counts SHALL appear alongside the input, output, and total token counts

#### Scenario: Inapplicable token metrics avoid noise

- **WHEN** a session has no cache-saved tokens or no thought tokens (for example a non-reasoning model with no cache hits)
- **THEN** the observability bar SHALL avoid rendering misleading or noisy zero-value metrics for those inapplicable fields
- **AND** the bar SHALL NOT misrepresent a non-accrued metric as an active reading

### Requirement: Usage summary in the sticky session title

The sticky session-title region (the header that remains visible while the transcript body scrolls) SHALL carry a usage摘要 so consumption and context health stay visible during transcript scroll. The摘要 SHALL include at minimum the total token count and the context-usage percentage, so a user can monitor usage and context health without scrolling back to the top of the transcript. The sticky title SHALL continue to display the session title, status, and turn count it already shows.

#### Scenario: Sticky title carries a usage summary while scrolling

- **WHEN** the transcript body is scrolled and the sticky session-title region is visible
- **THEN** the sticky title SHALL display a usage摘要 including at least the total token count and the context-usage percentage
- **AND** the摘要 SHALL remain visible while the transcript scrolls

#### Scenario: Sticky title retains existing identity information

- **WHEN** the sticky session-title region renders with the usage摘要 added
- **THEN** the sticky title SHALL continue to display the session title, status, and turn count
- **AND** the added usage摘要 SHALL NOT displace the existing identity information
