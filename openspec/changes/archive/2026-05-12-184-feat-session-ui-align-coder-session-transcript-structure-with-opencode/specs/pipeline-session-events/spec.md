## MODIFIED Requirements

### Requirement: Live session events converge with replayed transcript display

Pipeline session events SHALL update the visible transcript in a way that converges with the canonical replayed session detail after refresh, completion, interruption, or recovery.

#### Scenario: Live tool updates do not create transcript duplication

- **WHEN** live tool start and update events arrive for an in-flight session
- **THEN** the visible transcript updates the existing logical tool part instead of appending duplicate or orphan rows

#### Scenario: Recovery and interruption remain readable

- **WHEN** recovery, interruption, cancellation, or failure events occur during a session
- **THEN** the transcript renders readable divider or error states appropriate to the event
- **AND** non-fatal interruption states are not all rendered as fatal red failures

#### Scenario: Refresh after live activity preserves transcript meaning

- **WHEN** a live session receives updates and the page later refetches the canonical detail response
- **THEN** the visible transcript keeps equivalent turn order, grouping, and changed-file sections after the refetch
