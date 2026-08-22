### Requirement: Deterministic binding-reconcile refusals reach bounded terminal settlement

The durable runtime-event outbox SHALL track consecutive deterministic 4xx refusals independently for each `binding-reconcile` delivery key. A refusal is deterministic only for this explicit `(HTTP status, Server error code)` allowlist: `(409, conflict)`, `(409, agent_session_changed)`, `(409, workflow_agent_session_changed)`, `(409, workflow_runtime_binding_rejected)`, `(409, workflow_cleanup_binding_rejected)`, `(400, validation)`, `(400, runtime_session_id_required)`, `(400, session_runtime_identity_required)`, `(400, session_runtime_task_identity_invalid)`, and `(400, workflow_runtime_binding_required)`. The observed `(409, conflict)` response is included because the session runtime-events route currently emits that structured code. Unknown 4xx codes, 401/403, 404, 408, and 429 are retryable, as are 5xx, malformed, timeout, abort, and transport outcomes. After exactly three consecutive allowlisted refusals for the same key, the outbox MUST remove the pending records for that key from the durable queue, persist the removal, stop retrying those records, and emit one actionable error for that terminal settlement. A successful response SHALL retain the existing successful settlement behavior.

#### Scenario: Repeated deterministic refusal dead-letters one binding key
- **WHEN** a `binding-reconcile` delivery key receives deterministic 4xx responses on each retry until its bounded refusal threshold is reached
- **THEN** all pending records for that delivery key SHALL be removed from the outbox snapshot
- **AND** no later retry for that key SHALL be scheduled
- **AND** exactly one actionable terminal-settlement error SHALL be logged for that key

#### Scenario: A transient server failure remains retryable
- **WHEN** a `binding-reconcile` delivery receives a 5xx response, times out, or fails in transport
- **THEN** the pending records SHALL remain durable
- **AND** the outbox SHALL schedule a later retry using its existing retry timing
- **AND** the records SHALL not be dead-lettered solely because of that failure

#### Scenario: Refusal counters do not cross delivery keys
- **WHEN** two binding-reconcile keys receive deterministic 4xx responses with different session or runtime-binding identities
- **THEN** each key SHALL accumulate and settle its own refusal count independently
- **AND** reaching the terminal threshold for one key SHALL not remove or terminally settle the other key's records

### Requirement: Confirmed-consumed matching records settle without fabricating identity

A `matching-receipt` `session.input` or Workflow cleanup `session.cleanup` record SHALL settle as already consumed after two consecutive valid 2xx responses whose receipt arrays are empty. The first valid empty response SHALL retain the record for confirmation. The already-consumed settlement SHALL remove and durably persist removal of the record, SHALL not synthesize an Agent turn or positive receipt identity, and SHALL release any waiter from waiting indefinitely with an explicit terminal already-consumed outcome. Positive receipts SHALL continue to satisfy the existing event-type and identity checks before settlement.

The Workflow cleanup-turn endpoint SHALL use this receipt-array protocol. A newly recorded cleanup operation SHALL return a one-element array containing the validated `session.cleanup` receipt. An idempotent replay of an already persisted cleanup operation SHALL return HTTP 2xx with `[]` after rechecking the complete cleanup request identity; the Runner connection and delivery adapter SHALL preserve that empty array rather than synthesize a receipt.

#### Scenario: A lost Workflow input acknowledgement is confirmed consumed
- **WHEN** a pending `session.input` record receives a valid 2xx response with an empty receipt array twice consecutively
- **THEN** the outbox SHALL remove the record from the pending snapshot as already consumed
- **AND** the second empty response SHALL not be treated as a matching Agent turn receipt
- **AND** a waiter for that record SHALL receive a terminal already-consumed outcome rather than remain blocked

#### Scenario: A lost Workflow cleanup acknowledgement is confirmed consumed
- **WHEN** a pending `session.cleanup` record receives a valid 2xx response with an empty receipt array twice consecutively
- **THEN** the outbox SHALL remove the record from the pending snapshot as already consumed
- **AND** the second empty response SHALL not be treated as a matching Agent turn receipt
- **AND** a waiter for that record SHALL receive a terminal already-consumed outcome rather than remain blocked

#### Scenario: One empty response is not sufficient
- **WHEN** a pending matching-receipt record receives one valid 2xx response with an empty receipt array
- **THEN** the record SHALL remain pending and durable
- **AND** the outbox SHALL continue delivery until it receives a second consecutive valid empty response or a positive matching receipt

#### Scenario: A positive Workflow input receipt retains identity checks
- **WHEN** a `session.input` delivery returns a non-empty receipt array
- **THEN** the record SHALL settle only when a receipt has the submitted event type, the record's input delivery id, the expected AgentSession id, and a non-empty Agent turn id
- **AND** a receipt with a different delivery id, AgentSession id, or missing Agent turn id SHALL retain the record for retry

#### Scenario: An empty-confirmation sequence is interrupted
- **WHEN** a matching-receipt record receives an empty 2xx response followed by a non-empty non-matching response, malformed response, non-2xx response, timeout, or transport failure
- **THEN** the two-empty confirmation SHALL not be considered consecutive
- **AND** the record SHALL remain pending and retryable

### Requirement: Retention-cap enforcement warns once per crossing

The outbox SHALL preserve the existing retention cap and eviction policy: eligible streaming delta records SHALL be dropped first under pressure, non-delta facts SHALL not be dropped by that policy, and the existing durable snapshot representation SHALL remain in use. Observability SHALL emit one retention-cap warning when the retained queue crosses from at-or-below the cap to over-cap, SHALL suppress further warnings while that over-cap condition remains, and SHALL permit a new warning only after the queue returns to at-or-below the cap and crosses again.

#### Scenario: A saturated non-delta backlog does not flood warnings
- **WHEN** the retained queue exceeds the cap because non-delta records cannot be evicted and additional records are enqueued while it remains over the cap
- **THEN** the outbox SHALL retain the existing records and cap policy
- **AND** it SHALL emit one warning for that over-cap interval
- **AND** it SHALL not emit one additional cap warning for every enqueue in the same interval

#### Scenario: A later cap crossing is observable
- **WHEN** the queue first crosses the retention cap, later drains to at-or-below the cap, and then crosses the cap again
- **THEN** the outbox SHALL emit one warning for the first crossing and one warning for the later crossing
- **AND** it SHALL suppress duplicate warnings between those crossings

### Requirement: Historical settlement cannot starve live Workflow delivery

The runtime-event outbox SHALL provide bounded progress for a saturated recovered or newly enqueued backlog. It SHALL preserve FIFO ordering within a delivery sequence, while independent delivery groups SHALL receive bounded scheduling opportunities so a repeatedly failing historical group cannot starve other groups. Deterministically refused binding observations and confirmed-consumed matching records SHALL therefore converge without indefinitely blocking a live Workflow input receipt or stage transition.

#### Scenario: A saturated historical backlog drains while live input proceeds
- **WHEN** the outbox contains historical binding-reconcile refusals and already-consumed matching records together with a live Workflow `session.input`
- **AND** delivery timers advance through the refusal and two-empty confirmation boundaries
- **THEN** the historical records SHALL reach terminal settlement without manual deletion or retargeting
- **AND** the live Workflow input SHALL receive its valid matching receipt and complete its receipt wait
- **AND** the outbox SHALL retain only records that still have a retryable or unconfirmed delivery outcome

#### Scenario: One retrying group does not starve another group
- **WHEN** one delivery group repeatedly fails transiently while another group has a deliverable live Workflow record
- **THEN** the scheduler SHALL continue to grant the live group delivery opportunities under the configured bounded concurrency and retry timing
- **AND** the transiently failing record SHALL remain durable for retry
- **AND** the live record SHALL not wait indefinitely for the failing group to settle

#### Scenario: Recovered records use the same convergence rules
- **WHEN** the Runner restarts with pending records loaded from the durable outbox snapshot
- **THEN** binding-reconcile refusals, empty-receipt confirmations, retryable failures, and retention warnings SHALL follow the same rules as records enqueued after startup
- **AND** the recovered queue SHALL converge without operator deletion or a snapshot-format migration
