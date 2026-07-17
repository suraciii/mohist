### Requirement: Activity indicators are gated on session liveness

Streaming and thinking activity indicators (the transcript-level "Streaming..." indicator, the "Thinking..." placeholder, and the per-text-part streaming glyph) SHALL render only while the session is alive (running). The session's liveness/running signal is the authoritative gate: the streaming and thinking flags alone MUST NOT cause any activity indicator to render when the session is not running.

#### Scenario: Live session shows the streaming indicator

- **WHEN** the session is running and a streaming event arrives
- **THEN** the streaming activity indicator renders

#### Scenario: Live session shows the thinking indicator

- **WHEN** the session is running and the agent is thinking (no visible content yet)
- **THEN** the thinking activity indicator renders

### Requirement: A non-running session renders no activity indicators

A session that is not running (ended, completed, failed, cancelled, or inactive) MUST NOT render any streaming or thinking activity indicator, regardless of the value of the streaming/thinking flags. This holds even if a flag is lingering true after the session ended or after event replay on an already-ended session.

#### Scenario: Ended session never shows streaming or thinking indicators

- **WHEN** the session is not running (ended) and a streaming or thinking flag is true
- **THEN** no streaming indicator, thinking placeholder, or per-part streaming glyph is rendered

#### Scenario: Liveness gate overrides a lingering streaming flag

- **WHEN** the session ended hours ago (e.g. in a Finalizing/ended state) but the streaming flag is still true
- **THEN** no streaming activity indicator is rendered

### Requirement: Activity indicators are removed when a running session ends

When a running session transitions to not running (ends), any visible streaming or thinking activity indicator MUST be removed.

#### Scenario: Session ends mid-stream

- **WHEN** a running session that is showing a streaming indicator transitions to not running
- **THEN** the streaming and thinking activity indicators are removed

### Requirement: Per-text-part streaming glyph respects the liveness gate

The streaming glyph rendered on an assistant text part SHALL appear only while the session is running and the part is incomplete or actively streaming. A completed text part, or any text part in a non-running session, MUST NOT display a streaming glyph.

#### Scenario: Completed text part in an ended session shows no glyph

- **WHEN** a text part is completed and the session is not running
- **THEN** the text part renders no streaming glyph
