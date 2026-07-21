### Requirement: Runtime events remain pending until positively accepted

The originating runner SHALL durably retain every Workflow turn input, normalized runtime event, and terminal event, and every follow-up `session.input`, before its first network delivery attempt. The retained event MUST preserve its original AgentSession target, physical runtime session identity, payload, and position in its event sequence.

For these content and Workflow terminal events, the runner SHALL remove a retained event only after the runtime-events endpoint returns a receipt that positively accepts that event. A transport error, timeout, unsuccessful HTTP response, or successful HTTP response without a matching acceptance receipt MUST leave the event pending for another delivery attempt.

The Workflow reporter SHALL start local enqueue operations in production order as events are observed and MUST wait for all of those local writes before returning the turn result, without waiting for Server delivery. Restart recovery applies after an event's local enqueue has completed; recovering an event when the runner process terminates before that local commit completes is outside this change.

#### Scenario: Failed upload is eventually accepted

- **WHEN** a retained Workflow runtime event fails its first upload and a later attempt receives a matching acceptance receipt
- **THEN** the runner SHALL keep the event pending after the failed attempt
- **AND** SHALL remove it from pending delivery only after the matching receipt

#### Scenario: Empty receipt does not acknowledge delivery

- **WHEN** a runtime-events endpoint returns a successful HTTP response with no acceptance receipt for the submitted event
- **THEN** the runner MUST treat the event as unaccepted
- **AND** SHALL retain it for another delivery attempt

#### Scenario: Follow-up input upload fails

- **WHEN** uploading a follow-up `session.input` fails after the runner has accepted the follow-up for execution
- **THEN** the runner SHALL retain the input with its original Session target and physical runtime session identity
- **AND** SHALL continue delivery attempts until the endpoint positively accepts it

#### Scenario: Workflow activity is locally pending while the turn runs

- **WHEN** a Workflow turn observes runtime events whose local enqueue operations are still in progress
- **THEN** the runtime SHALL continue without waiting for Server acceptance
- **AND** the Workflow result MUST NOT return until those local enqueue operations have settled

### Requirement: Pending delivery survives runner restart and reconnection

The runner SHALL load and resume runtime-event deliveries whose local enqueue completed before process restart without requiring another Workflow turn or follow-up. The runner SHALL also resume pending delivery after reconnecting to the server. Recovery MUST use the event's recorded target and physical runtime session identity; it MUST NOT retarget the event to a replacement runtime session or transfer it to another runner.

#### Scenario: Runner restarts with undelivered events

- **WHEN** a runner stops after durably retaining events whose uploads have not been accepted
- **THEN** the restarted runner SHALL recover those events with their original targets, runtime session identities, payloads, and sequence positions
- **AND** SHALL resume delivery when the server is available

#### Scenario: Server connection recovers without runner restart

- **WHEN** pending events remain after a server disconnection and the runner reconnects
- **THEN** the runner SHALL resume delivery without waiting for new work to arrive

#### Scenario: Original runtime binding is no longer current

- **WHEN** the server does not accept a recovered event because its recorded physical runtime session identity is stale
- **THEN** the runner SHALL retain the event against that original identity
- **AND** MUST NOT attach or redeliver it under the replacement runtime session identity

### Requirement: Delivery preserves event order without coupling independent sessions

For each AgentSession event sequence, the runner SHALL attempt delivery in production order. A turn's `session.input` MUST be positively accepted before its assistant, reasoning, tool, usage, model, or terminal events can be delivered, and its terminal event MUST follow all preceding events from that turn. An unaccepted event MUST prevent later events in the same sequence from overtaking it, but MUST NOT prevent pending sequences for other AgentSessions from making progress.

#### Scenario: Middle event fails during a Workflow turn

- **WHEN** a Workflow turn's input is accepted, a later activity event fails delivery, and more activity plus a terminal event are produced
- **THEN** recovery SHALL deliver the failed activity before the later activity and terminal event
- **AND** the accepted sequence SHALL preserve the turn's production order

#### Scenario: One session remains unaccepted

- **WHEN** one AgentSession has an unaccepted pending event while another AgentSession has deliverable pending events
- **THEN** the first sequence SHALL preserve its order
- **AND** the second AgentSession's sequence SHALL remain eligible for delivery

### Requirement: Event delivery does not control runtime execution

Successful Server delivery MUST NOT be a prerequisite for starting or completing the Workflow OpenCode turn or follow-up that produced the event. Server upload failure, retry, or an unavailable server MUST NOT alter the runtime result, replace the runtime failure, cause the runtime prompt to execute more than once, or delay returning the Workflow or follow-up execution result until delivery succeeds.

Local outbox persistence is a separate precondition: the originating `session.input` MUST be durably enqueued before a Workflow prompt or follow-up runtime invocation starts. If that local enqueue fails, the runner MUST report the execution as unavailable and MUST NOT invoke the runtime without a durable input record. Local persistence of activity and terminal events produced after runtime start MUST settle before the Workflow result returns, but MUST NOT replace the runtime result with a Server delivery failure.

#### Scenario: Workflow event delivery remains unavailable

- **WHEN** Workflow events are locally durable but their Server uploads fail or remain pending while the OpenCode turn completes
- **THEN** the Workflow turn SHALL return the result determined by the OpenCode runtime without waiting for successful event delivery
- **AND** the pending events SHALL remain eligible for later delivery

#### Scenario: Follow-up input delivery remains unavailable

- **WHEN** a follow-up input is durably enqueued locally but its Server upload remains pending while the runtime accepts and executes the prompt
- **THEN** the follow-up SHALL execute exactly once without waiting for Server transcript persistence
- **AND** delivery retries MUST NOT invoke the runtime prompt again

#### Scenario: Follow-up input cannot be persisted locally

- **WHEN** the runner cannot durably enqueue a follow-up `session.input`
- **THEN** it SHALL report the follow-up as unavailable
- **AND** MUST NOT invoke the runtime prompt

### Requirement: Existing follow-up terminal delivery remains durable

Applying durable delivery to follow-up input MUST NOT remove or weaken durable delivery of `session.followup_completed` and `session.followup_failed` outcomes that carry an operation identity. These terminal outcomes SHALL preserve their existing operation-fenced settlement semantics: a transport error, timeout, unsuccessful HTTP response, or malformed response keeps the outcome pending, while a successful HTTP response containing a valid receipt array settles it even when that array is empty or has no matching receipt. A missing receipt after successful replay MUST NOT leave the terminal outcome permanently fencing the Session queue.

#### Scenario: Follow-up input and terminal outcome both encounter failures

- **WHEN** a follow-up input upload fails and its operation-correlated terminal outcome also fails to upload
- **THEN** the runner SHALL retain both facts for eventual delivery
- **AND** input recovery MUST NOT discard or replace the terminal outcome

#### Scenario: Follow-up terminal response is lost after Server acceptance

- **WHEN** the Server applies an operation-correlated follow-up terminal outcome but its response is lost and replay returns a successful response with an empty receipt array
- **THEN** the runner SHALL settle the pending terminal outcome
- **AND** MUST NOT keep it indefinitely at the head of the Session queue
