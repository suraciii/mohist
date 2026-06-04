## ADDED Requirements

### Requirement: Session event projection module supports timeline and compact views
The web client SHALL provide one shared session-event projection module at `entities/session/model/view.ts`. The module MUST expose `viewSessionEvents(events, kind)`, where `kind` supports `chat`, `timeline`, and `compact`, and all variants SHALL derive their output from the same raw `SessionEvent[]` stream.

#### Scenario: Chat projection derives transcript turns
- **WHEN** `viewSessionEvents(events, 'chat')` receives a stream containing Mohist prompts, assistant message chunks, thought chunks, tool calls, and tool updates
- **THEN** it returns a chat view with ordered prompt-led turns, assistant text, reasoning, and tool parts

#### Scenario: Timeline projection derives timeline rounds
- **WHEN** `viewSessionEvents(events, 'timeline')` receives the same stream
- **THEN** it returns timeline-oriented groups suitable for SessionTimeline rendering
- **AND** it does not require `reconstructRoundsFromLogs` to independently parse the stream

#### Scenario: Compact projection derives summaries
- **WHEN** `viewSessionEvents(events, 'compact')` receives the same stream
- **THEN** it returns compact session summary data such as high-level counts or preview content needed by compact session surfaces
- **AND** it reuses the same event narrowing and ordering semantics as chat and timeline projections

### Requirement: Timeline reconstruction uses shared projection
Session timeline reconstruction SHALL call `viewSessionEvents(events, 'timeline')` instead of maintaining a separate `reconstructRoundsFromLogs` raw-event parser. Live and historical timeline rendering MUST converge for the same ordered raw session events.

#### Scenario: Historical timeline uses shared projection
- **WHEN** a completed session timeline is rendered after refresh
- **THEN** the timeline loads raw session events
- **AND** it calls `viewSessionEvents(events, 'timeline')` to produce timeline groups

#### Scenario: Duplicate projection logic is removed
- **WHEN** timeline reconstruction code is inspected
- **THEN** `reconstructRoundsFromLogs` is removed or reduced to a call-through wrapper around `viewSessionEvents`
- **AND** it does not contain independent event-type parsing for assistant chunks, thought chunks, tool calls, or Mohist prompts

#### Scenario: Live and historical timeline agree
- **WHEN** a live session receives events and the same ordered events are later loaded historically
- **THEN** timeline grouping, tool identity, and assistant content order remain equivalent
