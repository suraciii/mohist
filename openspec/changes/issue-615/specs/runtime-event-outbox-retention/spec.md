### Requirement: Outbox capacity is finite and admission is explicit
The Runner SHALL enforce a finite retention capacity over all logical runtime-event records retained in memory or in `.mohist/runner-state/runtime-events.json`, including protected records and streaming records. The default capacity SHALL be 5,000 records unless an explicit deployment configuration overrides it. Durable enqueue operations SHALL be awaitable and SHALL resolve only when the record has been admitted to the durable outbox, or return an explicit outcome identifying why admission was rejected or reduced. When protected capacity is exhausted, the outbox MUST reject or backpressure the admission without appending an unbounded record and without reporting that admission succeeded.

#### Scenario: Protected admission is rejected at capacity
- **WHEN** the outbox is at its configured capacity and a new `session.input`, terminal `session.activity`, or other protected record is produced
- **THEN** the enqueue operation SHALL complete with a structured protected-capacity outcome
- **AND** the record SHALL NOT be silently discarded, replaced, or appended beyond the configured bound
- **AND** the owning execution SHALL be able to await and observe the rejection

#### Scenario: Existing over-capacity state is loaded
- **WHEN** startup loads a snapshot that already exceeds the configured capacity because it contains retained protected records
- **THEN** the outbox SHALL retain every existing protected record with its original identity and payload
- **AND** SHALL expose protected-capacity pressure and reject further protected admissions until capacity is available
- **AND** SHALL NOT overwrite the snapshot with an empty or truncated protected-record state

### Requirement: Protected records remain exact and are not evicted by retention
The outbox SHALL protect `session.input`, terminal `session.activity`, `turn.failed`, and any non-streaming tool-call, usage, model, or binding-reconciliation fact unless that record has passed an explicitly defined lossless compaction rule. A protected record MUST preserve its local record ID, logical target, physical `runtimeSessionId`, turn and work identity, event type, payload, acknowledgement policy, and sequence position until the Server positively settles it. Retention enforcement MUST NOT drop a protected record merely to make room for another record.

#### Scenario: Protected facts coexist with streaming pressure
- **WHEN** streaming deltas fill the outbox beyond the retention target while an input, tool-call lifecycle, usage fact, and terminal activity are pending
- **THEN** the outbox SHALL retain the protected facts exactly
- **AND** SHALL use only an explicitly safe delta compaction or delta admission policy for the streaming records
- **AND** SHALL surface pressure when no safe capacity action remains

#### Scenario: Retained identity is replayed after restart
- **WHEN** a protected record remains pending and the Runner restarts before its receipt is accepted
- **THEN** the restarted outbox SHALL replay the same record ID, target, physical runtime session identity, payload, and sequence position
- **AND** SHALL NOT fabricate a replacement event or retarget the record to a newer binding

### Requirement: Turn boundaries and terminal activity fail closed
The outbox SHALL treat `session.input` and terminal `session.activity` records as fail-closed delivery facts. A record of either kind SHALL be removed only after the response positively acknowledges the submitted event type and all required delivery identity, including the exact input or record identity and applicable AgentSession and Agent turn identity. A timeout, transport failure, non-success response, malformed response, empty receipt, receipt with another type, or mismatched identity MUST retain the exact record for replay.

#### Scenario: Input delivery receives an empty or malformed response
- **WHEN** a `session.input` delivery times out, fails in transport, returns a non-success response, returns malformed data, or returns an empty receipt array
- **THEN** the input record SHALL remain pending with its original prompt and execution identity
- **AND** the Runner MUST NOT mark the input accepted or invoke a dependent runtime turn on the basis of that response

#### Scenario: Terminal activity receives a mismatched receipt
- **WHEN** a terminal `session.activity` response acknowledges a different event type, record identity, AgentSession, or Agent turn
- **THEN** the terminal activity SHALL remain pending
- **AND** the Runner MUST NOT fabricate a successful or failed terminal result
- **AND** a later retry SHALL submit the original terminal activity unchanged

### Requirement: Compaction preserves Server-visible transcript invariants
The outbox SHALL define a deterministic identity and equivalence rule before compacting any high-volume record. A compacted sequence SHALL be equivalent to the original sequence for every Server-visible transcript and domain invariant: turn boundaries and order, cumulative message and reasoning text for each part, tool-call count and final status for each `toolCallId`, usage and cost effects for each turn, model observations, binding identity, and terminal status and failure details. Compaction MUST NOT merge records across logical targets, physical runtime sessions, Agent turns, text parts, tool-call IDs, or sequence boundaries. Records that cannot be proven equivalent SHALL remain protected and consume capacity.

#### Scenario: Tool-call updates are compacted safely
- **WHEN** several pending `tool_call.updated` records belong to one `toolCallId` and their intermediate states can be replaced by a later state
- **THEN** the outbox SHALL retain a lossless compacted representation only if delivery preserves that call ID, its input, its final completed or failed status, and the Server-visible tool count
- **AND** a call whose lifecycle or final state cannot be preserved SHALL remain as protected records

#### Scenario: Usage updates are compacted without changing accounting
- **WHEN** multiple `usage.updated` records for one turn are pending
- **THEN** the outbox SHALL combine them only into a representation whose application produces the same token, reasoning, cache, and cost effects as the original ordered records
- **AND** the compacted record SHALL retain the original turn and runtime identity
- **AND** a usage sequence without a deterministic equivalent SHALL remain protected

#### Scenario: Binding reconciliation does not cross a physical identity
- **WHEN** multiple binding-reconciliation facts for one AgentSession are pending
- **THEN** the outbox SHALL coalesce only facts for the same physical runtime binding and logical sequence
- **AND** the resulting fact SHALL preserve the latest Server-visible activity state and its `runtimeSessionId`
- **AND** facts for different runtime bindings SHALL remain separately protected and ordered

### Requirement: Producer and observer paths surface admission failures
Durable enqueue, runtime-event reporting, synchronous runtime observers, and terminal settlement SHALL propagate protected-capacity admission failures as explicit awaitable outcomes. An observer callback MUST be able to record an admission failure without throwing synchronously into the runtime provider, and the owning action or command MUST observe the failure when its reporting boundary settles. The failure MUST NOT be silently converted into task success, task failure, a fabricated terminal event, or replacement of the runtime's actual result.

#### Scenario: Workflow input cannot be admitted
- **WHEN** protected capacity rejects the initial Workflow `session.input`
- **THEN** the Workflow action SHALL return an explicit execution-unavailable outcome
- **AND** MUST NOT invoke the runtime turn
- **AND** MUST NOT leave an uncommitted input eligible for later delivery

#### Scenario: Produced event admission fails after runtime start
- **WHEN** a synchronous runtime observer produces a tool, usage, or terminal event and protected capacity rejects its durable enqueue
- **THEN** the observer SHALL record an awaitable reporting failure for terminal settlement
- **AND** the Workflow result SHALL preserve the runtime's actual success or failure
- **AND** the reporting failure SHALL remain explicitly observable rather than being swallowed by settlement

#### Scenario: Follow-up input cannot be admitted
- **WHEN** protected capacity rejects a follow-up `session.input`
- **THEN** the command handler SHALL return an explicit unavailable or backpressured result
- **AND** MUST NOT invoke the follow-up runtime
- **AND** MUST NOT claim that the follow-up was accepted for execution

### Requirement: Overload diagnostics are structured and aggregated
The Runner SHALL emit structured diagnostics that distinguish protected-capacity pressure, unsafe or rejected compaction, receipt mismatch, transport failure, and timeout. Diagnostics SHALL include aggregate counts and actionable logical context such as the affected sequence, capacity, and pending count, while excluding runtime payload contents. Repeated failures for retained records MUST be aggregated by reason and sequence or emitted on a bounded transition or interval; the Runner MUST NOT emit one repeated warning for every retained record on every retry.

#### Scenario: Repeated transport failures are aggregated
- **WHEN** a sequence retries the same pending records through repeated transport failures
- **THEN** diagnostics SHALL identify transport failure separately from receipt mismatch and timeout
- **AND** the diagnostic stream SHALL report an aggregate count or bounded summary for the sequence
- **AND** it SHALL NOT emit one unbounded warning per retained record per retry

#### Scenario: Protected pressure is distinguishable from receipt rejection
- **WHEN** one sequence is blocked because capacity is exhausted and another is blocked because the Server returned mismatched receipts
- **THEN** diagnostics SHALL expose protected-capacity pressure and receipt mismatch as different reasons
- **AND** operators SHALL be able to identify the affected logical sequence and pending count without reading event payloads
