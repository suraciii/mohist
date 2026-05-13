## MODIFIED Requirements

### Requirement: Live transcript updates converge with replayed transcript display

Live SSE updates and replayed session detail SHALL converge on the same visible transcript structure so refresh does not materially change ordering, grouping, or tool identity.

#### Scenario: Live tool updates merge like replayed tools

- **WHEN** live tool start/update events arrive and the page later refetches canonical session detail
- **THEN** the transcript preserves equivalent tool identity, merge behavior, order, and grouping

#### Scenario: Terminal events reconcile to canonical transcript

- **WHEN** completion, failure, timeout, cancellation, or recovery terminal events are observed live
- **THEN** the frontend reconciles to the persisted transcript without losing text, tool updates, errors, or recovery markers

### Requirement: Persisted ordering fidelity improves for new session events

Newly persisted session stream events SHOULD preserve finer-grained ordering than second-level timestamps so transcript replay can represent reasoning, text, and tool interleaving more faithfully.

#### Scenario: New stream events retain sub-second ordering

- **WHEN** multiple transcript events are persisted within the same second for a new session
- **THEN** the stored event timestamps retain enough precision to distinguish their order
- **AND** existing historical sessions remain replayable without destructive migration
