### Requirement: Session closure transcript part type uses a single dot-separated vocabulary token

The transcript part type that marks session closure SHALL be the dot-separated token `session.closed` exclusively. The `TranscriptPartTypes.SessionClosed` constant SHALL carry the value `session.closed`, aligning with `RuntimeEventTypes.SessionClosed`. No underscore-separated variant constant (`session_closed`) SHALL exist in the `TranscriptPartTypes` vocabulary. The session closure part type SHALL be identical across the entire session chain: runtime event emission, transcript accumulation (write side), persistence, and every read-side projection.

#### Scenario: TranscriptPartTypes.SessionClosed equals the dot-separated token

- **WHEN** the `TranscriptPartTypes` constant class is inspected after the change
- **THEN** `SessionClosed` SHALL equal `session.closed`
- **AND** no constant with the value `session_closed` SHALL exist in that class

#### Scenario: Runtime event type and transcript part type agree

- **WHEN** the closure event vocabulary is inspected
- **THEN** `RuntimeEventTypes.SessionClosed` and `TranscriptPartTypes.SessionClosed` SHALL carry the same string value (`session.closed`)

### Requirement: Write side persists session closure parts with the dot-separated type

The transcript accumulator (`TranscriptAccumulator.ToTranscriptPartType`) SHALL map a `session.closed` runtime event to a transcript part whose type is `session.closed`. New transcript parts persisted after this change SHALL carry the dot-separated type, not the underscore-separated type.

#### Scenario: Accumulated closure part carries the dot-separated type

- **WHEN** a `session.closed` runtime event is accumulated and flushed into a transcript part
- **THEN** the resulting part's `Type` SHALL be `session.closed`

### Requirement: Read side recognizes only the dot-separated closure type

Every read-side consumer that matches session-closure transcript parts SHALL recognize only `session.closed`. No read-side code SHALL contain dual-spelling acceptance logic of the form `Type == "session_closed" || Type == "session.closed"`. Specifically:
- `AgentSessionQuerier.ReadTerminalStateAsync` SHALL match only `session.closed`.
- `AgentSessionQuerier.LoadTerminalFactsAsync` SHALL filter closure parts by the dot-separated constant only.
- `TerminalFact.FromTranscript` SHALL select closure parts by the dot-separated constant only.
- `TranscriptEventSummaryProjector` SHALL match closure events by the dot-separated constant only.
- `SessionTranscriptBuilder` SHALL compare closure parts against the dot-separated constant (not the literal `"session_closed"`).
- `AgentSessionSummaryBuilder` SHALL compare closure parts against the dot-separated constant (not the literal `"session_closed"`).

#### Scenario: ReadTerminalStateAsync matches only the dot-separated type

- **WHEN** `ReadTerminalStateAsync` queries transcript parts for the most recent closure event
- **THEN** the query SHALL filter by `session.closed` only
- **AND** the query SHALL NOT include an `|| "session_closed"` alternative

#### Scenario: Terminal-fact loading matches only the dot-separated type

- **WHEN** terminal facts are loaded from transcript parts
- **THEN** the closure-part filter SHALL use the single `session.closed` constant
- **AND** it SHALL NOT accept the underscore spelling

#### Scenario: Transcript builder renders closure parts using the dot-separated type

- **WHEN** `SessionTranscriptBuilder` encounters a session-closure transcript part
- **THEN** it SHALL match the part by the `session.closed` type (via the shared constant, not a literal `"session_closed"`)
- **AND** it SHALL render the failure/cancellation error part identically to before for matching parts

#### Scenario: Summary builder extracts closure context using the dot-separated type

- **WHEN** `AgentSessionSummaryBuilder` extracts prior failure and key decisions from transcript parts
- **THEN** it SHALL match closure parts by the `session.closed` type (via the shared constant, not a literal `"session_closed"`)

#### Scenario: Event summary projector matches closure events using the dot-separated type

- **WHEN** `TranscriptEventSummaryProjector` summarizes transcript events
- **THEN** it SHALL match closure events by the `session.closed` constant
- **AND** it SHALL extract the failure category identically to before for matching parts

### Requirement: No literal underscore closure type remains in session read-side code

After this change, no source file in the session read-side code path SHALL contain the string literal `"session_closed"` as a type comparison. All closure-type matching SHALL reference the unified `TranscriptPartTypes.SessionClosed` constant (or `RuntimeEventTypes.SessionClosed` where appropriate).

#### Scenario: No hardcoded underscore literal in read-side matchers

- **WHEN** the session services source files are searched for the literal string `"session_closed"` after the change
- **THEN** the search SHALL return zero matches in type-comparison contexts (documentation comments that reference the historical name are not type comparisons)
