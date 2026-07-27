### Requirement: AgentSession retains deferred persistence semantics
AgentSession runtime activity SHALL continue to use deferred persistence. A successful persistence operation MUST durably save pending session state and domain events before committing the associated transcript data, and MUST clear only the data that has been durably saved. A failed state or event save MUST quarantine the activation; a failed transcript save after state and events commit MUST retain only the pending transcript data for retry.

#### Scenario: Deferred persistence succeeds
- **WHEN** an AgentSession has pending state, domain events, and transcript data and its scheduled persistence operation succeeds
- **THEN** state and domain events MUST be saved before the transcript is committed, and all successfully saved pending data MUST be cleared

#### Scenario: Transcript persistence fails after state persistence
- **WHEN** an AgentSession persistence operation saves state and domain events but the transcript save fails
- **THEN** the saved state and events MUST NOT be appended again, the transcript data MUST remain pending for retry, and the persistence timer MUST remain active

### Requirement: Persistence completion is observable per operation
The AgentSession asynchronous persistence boundary SHALL provide test support with an awaitable observation for the specific persistence operation caused by pending session data. The observation MUST complete only after that operation succeeds or fails, and MUST distinguish a completed durable write from a failed or still-pending write.

#### Scenario: A test awaits a successful persistence operation
- **WHEN** a test causes AgentSession data to become pending and awaits the observation for that persistence operation
- **THEN** the observation MUST not complete until the operation has durably saved all data required for a successful flush

#### Scenario: A persistence operation fails
- **WHEN** the observed AgentSession persistence operation fails to save state, events, or transcript data
- **THEN** the observation MUST report that operation as unsuccessful, and it MUST NOT report a durable completion for data that remains pending

### Requirement: Production grain APIs exclude a test flush command
The AgentSession grain production interface and implementation MUST NOT expose `FlushForTestAsync` or another test-only command that forces pending persistence. Tests MUST await the persistence observation or drive the existing deterministic scheduling boundary; they MUST NOT use polling, wall-clock waits, `Task.Delay`, or an artificial flush command to determine persistence completion.

#### Scenario: A test verifies persisted transcript data
- **WHEN** a server test needs to read AgentSession transcript data produced by deferred persistence
- **THEN** it MUST wait for the corresponding completion observation or deterministically advance the existing scheduler, without invoking a grain test-flush command or polling storage

### Requirement: Unobserved production persistence has no added work
The completion observation SHALL be passive when no test support awaits it. Normal production AgentSession commands and timer callbacks MUST retain their current persistence timing, batching, ordering, and retry behavior without additional persistence operations or blocking work solely for observation.

#### Scenario: Production persistence has no observer
- **WHEN** an AgentSession persists pending data during normal production execution and no completion observation is awaited
- **THEN** it MUST perform the same persistence work and timer lifecycle as before the observation capability was added
