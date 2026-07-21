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

The runner SHALL load and resume runtime-event deliveries whose local enqueue completed before process restart without requiring another Workflow turn or follow-up. The runner SHALL also resume pending delivery after reconnecting to the server. Recovery MUST use the event's recorded target and physical runtime session identity and MUST NOT transfer it to another runner. A `matching-receipt` event MUST NOT be retargeted to a replacement runtime session. Operation-fenced follow-up terminal outcomes retain their separate successful-response settlement semantics and are not guaranteed to remain pending after a valid empty response caused by stale binding.

#### Scenario: Runner restarts with undelivered events

- **WHEN** a runner stops after durably retaining events whose uploads have not been accepted
- **THEN** the restarted runner SHALL recover those events with their original targets, runtime session identities, payloads, and sequence positions
- **AND** SHALL resume delivery when the server is available

#### Scenario: Server connection recovers without runner restart

- **WHEN** pending events remain after a server disconnection and the runner reconnects
- **THEN** the runner SHALL resume delivery without waiting for new work to arrive

#### Scenario: Content event's original runtime binding is no longer current

- **WHEN** the server does not accept a recovered `matching-receipt` event because its recorded physical runtime session identity is stale
- **THEN** the runner SHALL retain the event against that original identity
- **AND** MUST NOT attach or redeliver it under the replacement runtime session identity

#### Scenario: Follow-up terminal receives an empty stale-binding response

- **WHEN** an operation-fenced follow-up terminal upload receives a valid empty receipt array because its recorded runtime binding is stale
- **THEN** the runner SHALL settle that terminal record under its successful-response policy
- **AND** SHALL NOT infer that the terminal fact was persisted

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

A failed pre-execution input enqueue MUST remove that uncommitted input from the in-memory queue so later health recovery cannot deliver an input whose runtime invocation never occurred. A local persistence failure for activity or terminal facts produced after runtime start SHALL retain those facts in memory for autonomous snapshot recovery, mark the outbox unhealthy, and remain observable; after all enqueue promises settle, the Workflow result SHALL remain the original runtime result. Process loss before the recovery snapshot succeeds remains outside restart recovery.

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

#### Scenario: Workflow input cannot be persisted locally

- **WHEN** the runner cannot durably enqueue a Workflow turn's initial `session.input`
- **THEN** it SHALL return an explicit execution-unavailable result
- **AND** MUST NOT invoke the OpenCode runtime or later deliver that uncommitted input

#### Scenario: Produced Workflow facts encounter a local write failure

- **WHEN** multiple synchronous runtime callbacks produce activity events and local persistence later rejects one or more enqueue operations
- **THEN** the runner SHALL retain every produced fact in memory for persistence recovery and mark the outbox unhealthy
- **AND** after all enqueue operations settle, the Workflow result SHALL preserve the original runtime success or failure

### Requirement: Local outbox health recovers without new execution

After a transient snapshot read or write failure, the runner SHALL retry local persistence autonomously without requiring another Workflow turn, follow-up, or network delivery. Startup and server reconnection SHALL also trigger an idempotent health-recovery attempt. The outbox MUST remain unhealthy while its durable snapshot is unknown or behind its retained in-memory state, and SHALL become healthy only after a successful load or atomic snapshot containing every retained record. Restored health SHALL re-enable work claims and follow-up acceptance and SHALL resume pending network delivery.

#### Scenario: Snapshot write recovers while execution is gated

- **WHEN** a post-start snapshot write fails and no new work, follow-up, or event arrives
- **THEN** a scheduled local retry SHALL persist the complete retained in-memory state
- **AND** only that successful atomic snapshot SHALL restore outbox health and resume pending delivery

#### Scenario: Startup load recovers after the file becomes readable

- **WHEN** startup cannot read or parse the existing snapshot and a later autonomous or reconnect-triggered load succeeds
- **THEN** the runner SHALL remain unavailable for new execution until the successful load
- **AND** SHALL recover and deliver the loaded records without replacing the snapshot with an empty state

### Requirement: Existing follow-up terminal delivery remains durable

Applying durable delivery to follow-up input MUST NOT remove or weaken durable delivery of `session.followup_completed` and `session.followup_failed` outcomes that carry an operation identity. These terminal outcomes SHALL preserve their existing operation-fenced settlement semantics: a transport error, timeout, unsuccessful HTTP response, or malformed response keeps the outcome pending, while a successful HTTP response containing a valid receipt array settles it even when that array is empty or has no matching receipt. Because the current Server uses the same empty response for a consumed operation and a stale binding, this policy intentionally does not guarantee retention of a stale follow-up terminal record. A missing receipt after successful replay MUST NOT leave the terminal outcome permanently fencing the Session queue.

#### Scenario: Follow-up input and terminal outcome both encounter failures

- **WHEN** a follow-up input upload fails and its operation-correlated terminal outcome also fails to upload
- **THEN** the runner SHALL retain both facts for eventual delivery
- **AND** input recovery MUST NOT discard or replace the terminal outcome

#### Scenario: Follow-up terminal response is lost after Server acceptance

- **WHEN** the Server applies an operation-correlated follow-up terminal outcome but its response is lost and replay returns a successful response with an empty receipt array
- **THEN** the runner SHALL settle the pending terminal outcome
- **AND** MUST NOT keep it indefinitely at the head of the Session queue
