## MODIFIED Requirements

### Requirement: Coder session surfaces use conversation transcript model

Session timeline and session detail surfaces that show coder sessions SHALL prefer the normalized conversation transcript model over event-log-style tool rows.

#### Scenario: Session link opens readable transcript

- **WHEN** a user opens `/issue/:number/session/:sessionId`
- **THEN** the primary view shows Mohist prompt summary, Coder response, grouped tools, file summaries, errors, and conclusion in conversation order

#### Scenario: Issue-level session summaries do not replace transcript

- **WHEN** coder session activity is summarized elsewhere in the issue UI
- **THEN** links and summaries may point to the detailed transcript
- **AND** the detailed session page remains the source for full readable conversation replay
