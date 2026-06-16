## ADDED Requirements

### Requirement: Compaction configuration is passed to ACP sessions

The system SHALL pass compaction configuration to ACP sessions when creating connections. Configuration SHALL include a usage threshold (as percentage of context window) that triggers compaction and a compaction strategy (summary-based). When no explicit configuration is provided, the system SHALL use sensible defaults (80% threshold, summary strategy).

#### Scenario: Compaction config forwarded to ACP session
- **WHEN** `createAcpConnection` is called with compaction config `{ threshold: 0.8, strategy: "summary" }`
- **THEN** the ACP session SHALL receive the compaction configuration
- **AND** the ACP agent SHALL initiate compaction when context window usage exceeds 80%

#### Scenario: Default compaction config used when not specified
- **WHEN** `createAcpConnection` is called without compaction config
- **THEN** the system SHALL use default values (threshold 0.8, strategy "summary")
- **AND** the ACP session SHALL operate with default compaction behavior

#### Scenario: Compaction config passed through plan and build stages
- **WHEN** Plan stage and Build stage create ACP connections
- **THEN** compaction configuration SHALL be included in both stage connection options
- **AND** compaction behavior SHALL be consistent across pipeline stages

### Requirement: Compaction events are captured and persisted

The system SHALL detect compaction events from the ACP server and persist them as session events. Each compaction event SHALL record the pre-compaction and post-compaction context window usage, the compaction strategy used, and a timestamp.

#### Scenario: Compaction event received and persisted
- **WHEN** the ACP server emits a compaction event with `{ contextWindowUsedBefore, contextWindowUsedAfter, strategy }`
- **THEN** the system SHALL persist the event as a session event with type `compaction`
- **AND** the event SHALL include pre/post usage metrics and strategy

#### Scenario: Compaction event updates session context metrics
- **WHEN** a compaction event is persisted
- **THEN** the session's `contextWindowSize` and `contextWindowUsed` fields SHALL be updated to reflect post-compaction values
- **AND** subsequent context health queries SHALL return the updated usage

### Requirement: Compaction does not lose essential context

Compaction SHALL use summary-based strategy that preserves critical context (task instructions, key decisions, error messages, and session memories) while reducing overall token count. After compaction, the agent SHALL retain enough context to continue meaningful work.

#### Scenario: Compaction preserves task-critical context
- **WHEN** auto-compaction triggers during a Plan session
- **THEN** the summarized context SHALL retain task instructions, artifact specifications, and error recovery information
- **AND** the agent SHALL continue producing expected artifacts after compaction

#### Scenario: Compaction reduces token usage measurably
- **WHEN** a compaction event completes
- **THEN** `contextWindowUsed` after compaction SHALL be less than `contextWindowUsed` before compaction
- **AND** the reduction SHALL bring usage below the compaction threshold
