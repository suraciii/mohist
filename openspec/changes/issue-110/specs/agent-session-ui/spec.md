## ADDED Requirements

### Requirement: Session transcript renders compaction events as timeline entries

The session transcript SHALL render compaction events as discrete, readable timeline entries. Each compaction entry SHALL display the compaction time, the strategy used, and the token reduction (before → after). Compaction entries SHALL be visually distinct from prompt-led turns so they do not disrupt the conversation reading flow.

#### Scenario: Compaction event shown in transcript timeline
- **WHEN** a session transcript includes a compaction event (950K → 400K tokens, summary strategy)
- **THEN** the timeline SHALL show a compaction entry with text like "Context compacted: 950K → 400K tokens (summary)"
- **AND** the entry SHALL be visually distinct from conversation turns (e.g., a compact info banner rather than a prompt/response pair)

#### Scenario: Multiple compactions render as separate entries
- **WHEN** a session underwent 3 compaction events
- **THEN** the transcript timeline SHALL show 3 distinct compaction entries
- **AND** each entry SHALL display its own before/after token counts

#### Scenario: Session without compaction shows no compaction entries
- **WHEN** a session transcript has no compaction events
- **THEN** the timeline SHALL NOT show any compaction entries
- **AND** no empty compaction section SHALL be rendered

### Requirement: Session page metadata includes context health status

The session page metadata area (model, duration, turn counts, session state) SHALL include the current context health status. The display SHALL show the usage percentage with color coding, positioned alongside other session metadata.

#### Scenario: Context health in session metadata
- **WHEN** a user views a completed session with context usage at 45%
- **THEN** the metadata area SHALL include "Context: 45% used" with a green indicator
- **AND** the indicator SHALL be positioned near other metadata like model and duration

#### Scenario: Context health updates in live session metadata
- **WHEN** a live session receives context health updates via SSE
- **THEN** the metadata area SHALL update the context percentage and color in real time
