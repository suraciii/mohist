# OpenSpec Capability: agent-session-persistence

## ADDED Requirements

### Requirement: AppendRuntimeEventsAsync returns without database writes

`AgentSessionGrain.AppendRuntimeEventsAsync` SHALL perform only in-memory accumulation and return to the runner immediately without awaiting any database write.

#### Scenario: Tool call event arrives
- **WHEN** the runner posts a `tool_call` runtime event to `AppendRuntimeEventsAsync`
- **THEN** the method SHALL return HTTP success before any state store or transcript store write is executed
- **AND** all persistence SHALL be deferred to a subsequent background operation

#### Scenario: Usage event arrives
- **WHEN** the runner posts a `usage` runtime event to `AppendRuntimeEventsAsync`
- **THEN** the method SHALL update in-memory domain state and return without invoking `_stateStore.SaveAsync()`

#### Scenario: Liveness event arrives
- **WHEN** the runner posts a liveness runtime event to `AppendRuntimeEventsAsync`
- **THEN** the method SHALL record activity in memory and return without database I/O

### Requirement: TranscriptAccumulator accepts events with void return

`TranscriptAccumulator.Accept(session, entries, now)` SHALL return void and accumulate text deltas and part deltas internally.

#### Scenario: Text delta is accumulated
- **WHEN** `Accept` receives a text delta entry
- **THEN** the delta text SHALL be appended to the internal `_pending` buffer
- **AND** no transcript part SHALL be emitted yet

#### Scenario: Part delta is accumulated
- **WHEN** `Accept` receives a part delta entry such as a tool or reasoning part
- **THEN** the part delta SHALL be appended to the internal `_accumulatedParts` collection
- **AND** the part SHALL remain pending until the next flush

#### Scenario: Continuous text accumulation
- **WHEN** multiple text delta entries arrive across separate `Accept` calls
- **THEN** all deltas SHALL concatenate into the same `_pending` buffer
- **AND** the buffer SHALL only be converted to parts during `BuildFlush()`

### Requirement: TranscriptAccumulator exposes two-phase flush interface

`TranscriptAccumulator` SHALL expose `BuildFlush(session, now)` to peek at pending state and `CommitFlush()` to clear accumulated state only after successful persistence.

#### Scenario: BuildFlush returns combined flush without clearing accumulated parts or input tracking
- **WHEN** `BuildFlush(session, now)` is called with pending text and accumulated parts
- **THEN** it SHALL convert `_pending` text into parts, combine them with `_accumulatedParts`, and return an `AgentSessionTranscriptFlush` containing the combined data
- **AND** `_pending` SHALL be cleared
- **AND** `_accumulatedParts` and input tracking SHALL remain unchanged

#### Scenario: CommitFlush clears accumulated state
- **WHEN** `CommitFlush()` is called after a successful `BuildFlush` and persistence
- **THEN** it SHALL clear `_accumulatedParts`, `_pending`, `_promptText`, `_promptKind`, and `_inputCreatedAt`

#### Scenario: Failed flush remains retryable
- **WHEN** `BuildFlush` returns a flush but persistence fails
- **THEN** `CommitFlush` SHALL NOT be called
- **AND** a subsequent `BuildFlush` SHALL return the same accumulated data for retry

### Requirement: AgentSessionGrain uses one-shot Orleans timer for deferred persistence

`AgentSessionGrain` SHALL register a one-shot Orleans timer with a 200ms due time and no period when domain state or transcript data becomes dirty.

#### Scenario: State becomes dirty
- **WHEN** `RecordActivity`, `ApplyUsage`, or `ResolveModel` mutates grain domain state
- **THEN** `_stateDirty` SHALL be set to true
- **AND** a one-shot timer SHALL be registered if not already active

#### Scenario: Transcript data becomes dirty
- **WHEN** `TranscriptAccumulator.Accept` accumulates text or part deltas
- **THEN** a one-shot timer SHALL be registered if not already active
- **AND** persistence SHALL run after the 200ms delay

#### Scenario: Timer fires once per batch
- **WHEN** multiple events accumulate within 200ms
- **THEN** only one timer callback SHALL execute and flush the combined batch
- **AND** no periodic timer SHALL be created

### Requirement: PersistCallback retries on failure

The timer callback `PersistCallback` SHALL save state and transcript only when both succeed, clear accumulator state, and retry on the next tick if either fails.

#### Scenario: State save succeeds and transcript save succeeds
- **WHEN** `_stateDirty` is true and `_transcript.BuildFlush()` returns a non-null flush
- **THEN** `_stateStore.SaveAsync()` SHALL be awaited
- **AND** `_transcriptStore.SaveAsync()` SHALL be awaited with the flush
- **AND** `_transcript.CommitFlush()` SHALL be called
- **AND** `_stateDirty` SHALL be set to false
- **AND** the timer SHALL be disposed

#### Scenario: State save fails
- **WHEN** `_stateStore.SaveAsync()` throws an exception
- **THEN** `_transcript.CommitFlush()` SHALL NOT be called
- **AND** `_stateDirty` SHALL remain true
- **AND** the timer SHALL remain active to retry

#### Scenario: Transcript save fails
- **WHEN** `_transcriptStore.SaveAsync()` throws an exception
- **THEN** `_transcript.CommitFlush()` SHALL NOT be called
- **AND** the timer SHALL remain active to retry

#### Scenario: No dirty state and no transcript flush
- **WHEN** `_stateDirty` is false and `_transcript.BuildFlush()` returns null
- **THEN** no store method SHALL be invoked
- **AND** the timer SHALL be disposed

### Requirement: Persistence failures are logged with session context

All persistence sites SHALL log structured errors using `_log.LogError` that include the session ID, part counts, and exception details.

#### Scenario: State save failure is logged
- **WHEN** `_stateStore.SaveAsync()` throws an exception
- **THEN** `_log.LogError` SHALL be called with the session ID
- **AND** the log SHALL include the exception message and stack trace

#### Scenario: Transcript save failure is logged
- **WHEN** `_transcriptStore.SaveAsync()` throws an exception
- **THEN** `_log.LogError` SHALL be called with the session ID
- **AND** the log SHALL include the number of parts being saved
- **AND** the log SHALL include the exception message and stack trace

### Requirement: OnDeactivateAsync flushes remaining state and transcript synchronously

`OnDeactivateAsync` SHALL dispose the timer and synchronously flush any remaining dirty state and transcript before returning.

#### Scenario: Pending state and transcript on deactivation
- **WHEN** the grain is deactivated while `_stateDirty` is true and transcript data is pending
- **THEN** the timer SHALL be disposed
- **AND** `_stateStore.SaveAsync()` SHALL be invoked
- **AND** `_transcript.BuildFlush()` and `_transcriptStore.SaveAsync()` SHALL be invoked if a flush exists
- **AND** `_transcript.CommitFlush()` SHALL be called on success

#### Scenario: Deactivation flush failure is logged
- **WHEN** the synchronous deactivation flush throws an exception
- **THEN** `_log.LogError` SHALL be called with the session ID and exception details
- **AND** the grain deactivation SHALL complete without propagating the exception

### Requirement: session.input prompt info is captured for the next flush

`TranscriptAccumulator.Accept` SHALL capture prompt text, kind, and timestamp from `session.input` events and include them in the next `BuildFlush` turn.

#### Scenario: session.input arrives
- **WHEN** `Accept` receives a `session.input` entry containing prompt text and kind
- **THEN** `_promptText`, `_promptKind`, and `_inputCreatedAt` SHALL be set from the entry
- **AND** the values SHALL be included in the turn info of the next `BuildFlush` result

#### Scenario: Input info is cleared after commit
- **WHEN** `CommitFlush()` is called after a flush that included input-derived turn info
- **THEN** `_promptText`, `_promptKind`, and `_inputCreatedAt` SHALL be cleared

### Requirement: SavePartsAsync upserts by type and correlation key

`IAgentSessionTranscriptStore.SavePartsAsync` SHALL upsert transcript parts by `type + correlationKey` so retries do not create duplicate rows.

#### Scenario: Retry of failed flush
- **WHEN** a failed flush is retried and `SavePartsAsync` is called again with the same parts
- **THEN** existing rows with matching `type` and `correlationKey` SHALL be updated
- **AND** no duplicate transcript parts SHALL be created

#### Scenario: New correlation key creates new part
- **WHEN** `SavePartsAsync` receives a part with a `correlationKey` that does not exist for its type
- **THEN** a new transcript part row SHALL be inserted

### Requirement: SyncLabelsAsync logs warning on null labels

`IAgentSessionStore.SyncLabelsAsync` SHALL log a Warning when labels become null.

#### Scenario: Null labels are synchronized
- **WHEN** `SyncLabelsAsync` is called with null labels
- **THEN** `_log.LogWarning` SHALL be called with the session ID
- **AND** the method SHALL handle the null defensively without throwing
