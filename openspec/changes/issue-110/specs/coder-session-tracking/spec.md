## ADDED Requirements

### Requirement: Coder session records persist context window usage data

Persisted coder session records SHALL store `contextWindowSize` and `contextWindowUsed` fields. These fields SHALL be initialized when the session is created and updated when `usage_update` notifications arrive. The fields SHALL be included in session summary and detail API responses.

#### Scenario: Session record stores initial context metrics
- **WHEN** a coder session record is created
- **AND** the first `usage_update` notification reports `contextWindowSize: 1000000, contextWindowUsed: 5000`
- **THEN** the session record SHALL store `contextWindowSize: 1000000` and `contextWindowUsed: 5000`

#### Scenario: Session record updates context metrics on usage_update
- **WHEN** a subsequent `usage_update` notification reports `contextWindowUsed: 350000`
- **THEN** the session record SHALL update `contextWindowUsed` to 350000
- **AND** `contextWindowSize` SHALL remain 1000000

#### Scenario: Context metrics persisted across server restarts
- **WHEN** the server restarts and loads session records
- **THEN** each session record SHALL retain its last known `contextWindowSize` and `contextWindowUsed`
- **AND** the values SHALL be available in session detail API responses

### Requirement: Compaction events are persisted in session stream logs

Compaction events SHALL be persisted as `session_stream_log` rows with event type `compaction`. Each compaction log SHALL include `contextWindowUsedBefore`, `contextWindowUsedAfter`, `strategy`, and a timestamp. The compaction event SHALL appear in the session transcript alongside other timeline events.

#### Scenario: Compaction event persisted in stream log
- **WHEN** a compaction occurs in a tracked session
- **THEN** a `session_stream_log` row SHALL be inserted with `eventType: "compaction"`
- **AND** the row SHALL contain `data.contextWindowUsedBefore`, `data.contextWindowUsedAfter`, and `data.strategy`

#### Scenario: Compaction events rendered in session transcript
- **WHEN** the session transcript is assembled from stream logs
- **THEN** compaction events SHALL appear as discrete timeline entries
- **AND** each entry SHALL show the before/after token counts and strategy

### Requirement: Session summary list includes context health indicators

The coder session summary response (used by session list endpoints) SHALL include `contextWindowSize`, `contextWindowUsed`, and a computed `contextUsagePercent` field for each session. This enables session list views to render health indicators without loading full session detail.

#### Scenario: Session summary includes context usage
- **WHEN** the coder session list query returns sessions
- **THEN** each session summary SHALL include `contextWindowSize`, `contextWindowUsed`, and `contextUsagePercent` (computed as `used/size * 100`)
- **AND** these fields SHALL be available without loading per-session stream logs

#### Scenario: Session with no usage data returns zero values
- **WHEN** a session has never received a `usage_update` notification
- **THEN** `contextWindowSize` SHALL be 0, `contextWindowUsed` SHALL be 0, and `contextUsagePercent` SHALL be 0
- **AND** the session list SHALL render no health indicator for that session
