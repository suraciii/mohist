## ADDED Requirements

### Requirement: Compaction configuration is accepted in ACP session options

`AcpConnectionOptions` and `AcpSessionOptions` SHALL include an optional `compaction` field with `{ threshold: number, strategy: "summary" }`. When provided, the compaction configuration SHALL be forwarded to the ACP server session.

#### Scenario: Compaction config in AcpConnectionOptions
- **WHEN** `createAcpConnection` receives options with `compaction: { threshold: 0.8, strategy: "summary" }`
- **THEN** the ACP server session SHALL be configured with auto-compaction at 80% threshold

#### Scenario: No compaction config means no auto-compaction
- **WHEN** `createAcpConnection` receives options without the `compaction` field
- **THEN** the ACP server session SHALL NOT enable auto-compaction
- **AND** manual compaction via API remains available

### Requirement: Compaction events from ACP server are observed and forwarded

Agent runtime SHALL detect compaction events in ACP session notifications and forward them to the session observer pipeline. A compaction event SHALL include pre-compaction context usage, post-compaction context usage, and the strategy used.

#### Scenario: Compaction notification forwarded to observers
- **WHEN** the ACP server emits a compaction notification with usage before/after
- **THEN** the session observer SHALL receive the compaction event
- **AND** workflow log SHALL record the compaction event

#### Scenario: Context window usage updated after compaction
- **WHEN** a compaction event is processed
- **THEN** the session's tracked context window usage SHALL be updated to the post-compaction value
- **AND** subsequent health queries SHALL reflect the reduced usage

### Requirement: Context window usage data is tracked per session

Agent runtime SHALL track `contextWindowSize` and `contextWindowUsed` for each session using data from ACP `usage_update` notifications. This tracking SHALL persist across the session lifecycle for health reporting and exhaustion detection.

#### Scenario: usage_update notification updates tracked metrics
- **WHEN** an ACP session emits `usage_update` with `{ contextWindowSize: 1000000, contextWindowUsed: 320000 }`
- **THEN** the session's tracked metrics SHALL be updated to `contextWindowSize: 1000000, contextWindowUsed: 320000`

#### Scenario: Context metrics available for health queries
- **WHEN** a health check queries a session's context state
- **THEN** the response SHALL include the latest known `contextWindowSize` and `contextWindowUsed`
- **AND** these values SHALL be accurate within the last usage_update notification
