## MODIFIED Requirements

### Requirement: Session timeline uses opencode-like reading layout and live affordances

The session timeline SHALL use a centered reading column, sticky in-flow session header, subtle running indicators, and user-respecting scrolling behavior that matches an opencode-style transcript experience.

#### Scenario: Transcript layout prioritizes reading flow

- **WHEN** the session page renders on desktop or mobile
- **THEN** the transcript content is shown in a centered reading column rather than a dashboard-width layout
- **AND** the session title and running state remain visible via a sticky in-timeline header

#### Scenario: Running sessions show subtle progress

- **WHEN** the session is live or finalizing
- **THEN** the page shows an inline spinner and subtle progress treatment in the timeline header
- **AND** the UI does not rely on rapidly accumulating tool cards to communicate activity

#### Scenario: Auto-follow respects reader intent

- **WHEN** new transcript content arrives while the reader is bottom-locked
- **THEN** the timeline auto-follows the new content
- **BUT WHEN** the reader scrolls away, selects text, or scrolls inside a nested `data-scrollable` region
- **THEN** auto-follow pauses until the reader explicitly returns to the bottom
- **AND** a jump-to-bottom control appears only when it is needed
