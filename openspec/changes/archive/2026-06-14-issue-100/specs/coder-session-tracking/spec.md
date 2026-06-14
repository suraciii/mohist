# OpenSpec Capability: coder-session-tracking

## ADDED Requirements

### Requirement: AppendRuntimeEventsAsync defers persistence

`AgentSessionGrain.AppendRuntimeEventsAsync` SHALL return to the runner immediately after in-memory accumulation and SHALL NOT perform inline state store or transcript store writes.

#### Scenario: Runtime event batch is accepted
- **WHEN** the runner posts a batch of runtime events to `AppendRuntimeEventsAsync`
- **THEN** the grain SHALL accumulate state and transcript changes in memory
- **AND** the method SHALL return before `_stateStore.SaveAsync()` or `_transcriptStore.SaveAsync()` is invoked

#### Scenario: Realtime fan-out remains inline
- **WHEN** `AppendRuntimeEventsAsync` processes runtime events
- **THEN** `FanOutRealtimeAsync` SHALL be invoked inline before the method returns
- **AND** realtime subscribers SHALL receive events without waiting for deferred persistence

### Requirement: Deferred persistence preserves complete session transcript

The session detail page SHALL display a complete transcript for sessions processed with deferred persistence after the background flush commits.

#### Scenario: Transcript parts accumulate before flush
- **WHEN** multiple runtime events append transcript parts within the deferral window
- **THEN** `TranscriptAccumulator` SHALL hold all parts until `BuildFlush()` is called
- **AND** `CommitFlush()` SHALL only clear parts after both state and transcript saves succeed

#### Scenario: Session detail shows flushed transcript
- **WHEN** a user views the session detail page after the deferred persistence timer has committed
- **THEN** all transcript parts accumulated since the last commit SHALL be visible
- **AND** no committed transcript parts SHALL be missing

#### Scenario: Retry after failed flush preserves transcript completeness
- **WHEN** a deferred transcript flush fails and is retried
- **THEN** the retry SHALL rewrite the same `type + correlationKey` rows without creating duplicates
- **AND** the session detail page SHALL eventually show the complete transcript
