### Requirement: Every durable event producer triggers an immediate dispatch attempt

After durably appending one or more events, each WorkflowRun, Issue, Epic, AgentSession, and AgentJob producer SHALL request an immediate dispatch cycle. The request SHALL occur only after the event append has committed or otherwise completed successfully, and SHALL not invoke event handlers in the producer's write path.

#### Scenario: Epic event append wakes the dispatcher
- **WHEN** an Epic command commits state and one or more Epic event rows
- **THEN** the producer SHALL request an immediate dispatch cycle after the commit
- **AND** no matching event handler SHALL run as part of the Epic command

#### Scenario: AgentJob event append wakes the dispatcher
- **WHEN** an AgentJob durably appends its failure event
- **THEN** the AgentJob producer SHALL request an immediate dispatch cycle after the append succeeds
- **AND** the wake-up request SHALL not alter the event's payload, source, or identity

### Requirement: Immediate dispatch requests are best-effort latency optimization

An immediate dispatch request SHALL be fire-and-forget and SHALL not change the success or failure of the producer's committed work. The dispatcher's durable reminder SHALL remain the sole correctness mechanism for finding and delivering undelivered rows; a lost, delayed, or failed immediate request SHALL not strand an event.

#### Scenario: Immediate request succeeds
- **WHEN** a producer appends an event and the dispatcher is available
- **THEN** the dispatcher SHALL begin a dispatch cycle without waiting for the next reminder tick
- **AND** the event SHALL retain the same at-least-once delivery semantics

#### Scenario: Immediate request is lost
- **WHEN** a producer has durably appended an event but its immediate dispatch request fails or is not delivered
- **THEN** the producer operation SHALL remain successful
- **AND** the next dispatcher reminder tick SHALL query and deliver the undelivered event

### Requirement: Retry state follows configured exponential backoff within one process lifetime

For each matching handler of an undelivered event, the dispatcher SHALL track attempts independently and retry a failed handler using exponential backoff derived from `EventDispatcherOptions.BaseBackoff`, capped by `EventDispatcherOptions.MaxBackoff`, until `EventDispatcherOptions.MaxAttempts` is reached. Retry state SHALL remain process-local and SHALL reset when the dispatcher process restarts; retry attempts SHALL not be persisted or accumulated across restarts.

#### Scenario: Handler failures back off independently
- **WHEN** two handlers match an event and one handler fails while the other succeeds
- **THEN** only the failed handler SHALL consume retry attempts and wait for backoff
- **AND** the successful handler SHALL not be invoked again solely because the other handler is retrying

#### Scenario: Dispatcher restarts during retry
- **WHEN** a handler has pending retry attempts for an undispatched event and the dispatcher process restarts
- **THEN** the event SHALL remain eligible for reminder-driven delivery
- **AND** its handler retry count SHALL restart from zero in the new process

### Requirement: Delivery settlement preserves in-process retry progress until durable

Within a dispatcher process lifetime, the dispatcher SHALL retain an event's per-handler terminal and retry state until its delivered or dead-letter settlement is durably recorded. If that settlement write fails, the dispatcher SHALL retry the settlement without resetting the event's in-process handler attempt count or re-invoking handlers that already completed successfully.

#### Scenario: Dead-letter settlement persistence fails
- **WHEN** a handler has exhausted its configured retry budget and recording its dead-letter settlement fails
- **THEN** the event SHALL remain undispatched
- **AND** the next dispatch cycle SHALL retry settlement with the existing handler state rather than restart the handler's attempt count

### Requirement: Blocked source count is observable

The dispatcher SHALL expose the count of sources blocked in the most recent dispatch cycle by a pending handler retry as the `mohist.server.event_dispatcher.blocked_sources` OpenTelemetry observable gauge. The metric SHALL report a count only and SHALL NOT use source identifiers as metric attributes.

#### Scenario: Pending retry blocks later events from the same source
- **WHEN** an event has a handler awaiting its next retry time and a later undispatched event has the same source
- **THEN** the dispatcher SHALL not dispatch the later event in that cycle
- **AND** the blocked-source gauge SHALL report at least one blocked source

#### Scenario: No source is blocked
- **WHEN** a dispatch cycle has no source whose earlier event is awaiting retry
- **THEN** the blocked-source gauge SHALL report zero

### Requirement: Event bus documentation states the deployed delivery contract

The event bus design document SHALL describe handwritten, option-configured exponential backoff; best-effort producer wake-ups with reminder-backed correctness; process-local retry state that resets after restart; FIFO source blocking; and the blocked-source metric. It SHALL not describe Polly as the retry mechanism or claim that producers never wake the dispatcher.

#### Scenario: Operator consults the event bus design document
- **WHEN** an operator reads the event bus design document to diagnose a delayed event
- **THEN** the document SHALL identify retry backoff, restart-reset retry state, and FIFO source blocking as deployed behavior
- **AND** it SHALL identify the blocked-source metric as the visibility signal for stalled sources
