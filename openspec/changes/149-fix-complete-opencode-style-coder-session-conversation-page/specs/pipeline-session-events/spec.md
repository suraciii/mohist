## MODIFIED Requirements

### Requirement: Live events converge with historical transcript replay

Live SSE events and persisted session events SHALL carry enough consistent data that appending live updates and refetching historical detail produce the same visible transcript shape.

#### Scenario: Live tool event refetch parity

- **WHEN** the frontend receives live tool start/update events and later refetches session detail
- **THEN** the resulting transcript keeps equivalent tool identity, merge behavior, order, and grouping

#### Scenario: Live completion reconciles to persisted transcript

- **WHEN** a coder session terminal event is received live
- **THEN** the frontend can refetch or otherwise reconcile to the canonical persisted transcript without losing text, tool updates, errors, or recovery events

#### Scenario: Live drops are recoverable

- **WHEN** the browser misses a live SSE event but the event was persisted
- **THEN** refreshing or refetching the session detail restores the missing transcript part in the correct order
