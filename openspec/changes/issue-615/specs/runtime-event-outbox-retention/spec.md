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

### Requirement: Runtime-event requests have one strict identity-complete contract
Every workflow, generic, and Session runtime-event request SHALL carry `runtimeEventContractVersion: 2` and the same envelope: a tagged-union `logicalTarget` (`workflow` with project/run/session name, `generic` with project/session ID, or `session` with session ID), `runtime`, `runtimeSessionId`, explicit-null applicable AgentSession/Agent turn/input identity, and `runtimeEvents` entries containing `runtimeEventId`, `type`, and `payload`. The route-derived target SHALL equal the envelope target. The cleanup-turn request SHALL use the explicit single-record v2 variant with `runtimeEventId`, workflow `logicalTarget`, runtime/session identity, `taskRunId`, `workId`, `prompt`, and `cleanupOperationId`, where the two IDs are equal. Every acceptance SHALL carry `runtimeEventContractVersion`, `runtimeEventId`, type, logical target, physical runtime identity, and all applicable AgentSession/turn/input identities. Batch acceptance SHALL return exactly one positional receipt per submitted event; cleanup-turn SHALL return a one-element receipt array. `runtimeEventId` SHALL be the durable Runner record ID; `InputDeliveryId` is equal to it for `session.input`. The AgentSession ledger SHALL distinguish durable `Pending` recovery from final `Accepted`: a matching pending ID returns no positive receipt, retries transcript persistence and any required binding/follow-up operation idempotently, and emits a receipt only after those effects and the final ledger state are durable. Missing, old, malformed, conflicting, or count-mismatched identity fails closed and retains the original record.

#### Scenario: A replay returns the original acceptance
- **WHEN** the same v2 `runtimeEventId` and identical fingerprint are posted again through a workflow, generic, Session, or cleanup route
- **THEN** the AgentSession grain SHALL return the persisted receipt byte-for-byte equivalent in its identity fields
- **AND** SHALL apply no second domain event, transcript event, binding operation, or follow-up dispatch

#### Scenario: A duplicate and new record share one batch
- **WHEN** a batch contains an already accepted record followed by a new record with valid identity
- **THEN** the AgentSession grain SHALL retain the stored receipt for the duplicate and prepare the new record once with its `Pending` ledger entry in one atomic state commit
- **AND** the response SHALL preserve one positional receipt per input after the new record's transcript and required side effects finalize as `Accepted`
- **AND** a conflicting reuse of an existing ID SHALL reject the whole batch without applying the new record

### Requirement: Acceptance and transcript persistence recover as one idempotent boundary
The Server SHALL not issue a positive runtime-event receipt from AgentSession state alone. It SHALL prepare new domain state and a `Pending` acceptance entry in one state commit, then durably flush the corresponding transcript effect through an operation keyed by `runtimeEventId` before finalizing the entry as `Accepted`. The transcript store SHALL deduplicate that key, including after a crash between transcript flush and final ledger finalization. Workflow binding and follow-up dispatch SHALL use the same key for idempotent external operations. A failure or response loss at any boundary SHALL leave a recoverable pending entry and SHALL never produce a receipt for a missing transcript row or a duplicate transcript row.

#### Scenario: State commit succeeds but transcript flush fails
- **WHEN** a new runtime event has a durable `Pending` ledger entry and AgentSession state but its transcript flush fails or the Server crashes before the flush
- **THEN** the route SHALL return no positive receipt and the Runner record SHALL remain pending
- **AND** a retry with the same `runtimeEventId` SHALL perform one durable transcript write without reapplying the domain event
- **AND** the final receipt SHALL be issued only after the transcript and `Accepted` ledger state are durable

#### Scenario: Transcript flush succeeds before final ledger state
- **WHEN** the transcript store durably writes a runtime event but AgentSession finalization fails or the response is lost
- **THEN** the acceptance ledger SHALL remain `Pending` and the transcript store SHALL recognize the same key as already applied
- **AND** a retry SHALL not append a second transcript row and SHALL finalize the original receipt once

#### Scenario: Generic, Session, Workflow, and cleanup routes do not acknowledge deferred flushes
- **WHEN** any of those routes receives a runtime event whose transcript persistence is still deferred or fails
- **THEN** the route SHALL return no positive receipt
- **AND** the event SHALL remain recoverable by its original `runtimeEventId`

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
The first release SHALL compact only adjacent `message.delta` or `reasoning.delta` records whose payload contains a non-empty `text`, `partId`, and `messageId`, and whose applicable `turnId`, logical target, physical `runtimeSessionId`, and logical lane key all match. The reducer SHALL concatenate text in original order, retain the earliest representative `runtimeEventId`, retain every replaced source ID and `compactedRawEventCount`, and preserve the Server transcript's cumulative text and raw-event count. The Server transcript accumulator SHALL interpret `compactedRawEventCount` as the number of source delta rows for its existing `RawEventCount` and flush thresholds.

Tool-call, usage, model, and binding-reconciliation records SHALL have no compaction reducer in this change. They remain protected, even when they share an ID or appear replaceable. A missing identity, unsupported payload shape, non-adjacent record, different physical runtime, different Agent turn, different text part, different logical target, or any protected event type SHALL consume capacity unchanged and emit an `unsafe-compaction` diagnostic. No compaction may alter turn boundaries, FIFO order, tool-call lifecycle/count, usage/cost accounting, model observations, binding identity, or terminal details.

#### Scenario: Adjacent text deltas are compacted losslessly
- **WHEN** adjacent pending text deltas share event type, target, runtime session, turn, logical lane key, `partId`, and `messageId`
- **THEN** the outbox MAY replace them with one representative record whose text is their ordered concatenation
- **AND** the representative SHALL retain all source IDs and their count
- **AND** the Server transcript SHALL contain the same cumulative text and raw-event count as delivery of the source rows separately

#### Scenario: Tool-call updates remain protected
- **WHEN** several pending `tool_call.started`, `tool_call.updated`, or `tool_call.completed` records belong to one `toolCallId`
- **THEN** the outbox SHALL retain each record as protected in this change
- **AND** capacity pressure SHALL reject a new record rather than replace a lifecycle state
- **AND** Server tests SHALL prove that cumulative tool count, input, and final status are unchanged when no compaction occurs

#### Scenario: Usage updates remain protected
- **WHEN** multiple `usage.updated` records for one turn are pending
- **THEN** the outbox SHALL retain each record as protected in this change
- **AND** capacity pressure SHALL reject a new record rather than sum, replace, or otherwise rewrite a usage payload
- **AND** Server tests SHALL prove that `AgentSession.ApplyUsage` continues to add every token, reasoning, cache, and cost field exactly once

#### Scenario: Binding reconciliation remains physical-identity fenced
- **WHEN** multiple binding-reconciliation facts for one AgentSession are pending
- **THEN** the outbox SHALL retain each record as protected in this change
- **AND** facts for different runtime bindings SHALL remain separately protected and ordered
- **AND** no retry or retention operation SHALL rewrite a record's `runtimeSessionId`

### Requirement: Producer and observer paths surface admission failures
Durable enqueue, runtime-event reporting, synchronous runtime observers, and terminal settlement SHALL propagate protected-capacity admission failures as explicit awaitable outcomes. An observer callback MUST be able to record an admission failure without throwing synchronously into the runtime provider, and the owning action or command MUST observe the failure when its reporting boundary settles. The failure MUST NOT be silently converted into task success, task failure, a fabricated terminal event, or replacement of the runtime's actual result. For AgentJob launch input, the gate applies both to a new input and to a coordinator-owned input already present in AgentSession state.

#### Scenario: AgentJob input is admitted before runtime invocation
- **WHEN** an AgentJob has an AgentSession and its OpenCode or Pi physical session has been resolved or created
- **THEN** the Runner SHALL attach that physical session, durably enqueue `session.input`, and await a positive identity-matching receipt before calling `runTurn`
- **AND** a protected-capacity rejection, persistence failure, attach failure, timeout, or mismatched receipt SHALL return `execution-unavailable`
- **AND** the corresponding runtime's `runTurn` SHALL be called zero times
- **AND** a later delivery retry SHALL reuse the same `runtimeEventId` and SHALL not invoke the runtime again

#### Scenario: Coordinator-owned AgentJob input is reconciled without duplication
- **WHEN** an AgentJob with an AgentSession carries `initialInputId` and `initialTurnId` because the coordinator already recorded its launch input and turn
- **THEN** OpenCode and Pi SHALL attach the resolved physical session before enqueueing a `session.input` record whose `runtimeEventId` is exactly `initialInputId`
- **AND** the Server SHALL verify the original prompt, turn, and identity fingerprint and complete the existing `Pending` acceptance entry without creating a second input, turn, or transcript row
- **AND** the executor SHALL await the positive receipt before `runTurn`, with any conflict, capacity, persistence, attach, timeout, or receipt failure producing `execution-unavailable` and zero `runTurn` calls
- **AND** all retries SHALL reuse `initialInputId` and the original physical runtime identity

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
