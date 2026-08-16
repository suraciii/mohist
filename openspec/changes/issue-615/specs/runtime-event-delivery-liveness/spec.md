### Requirement: Delivery scheduling isolates logical sequences
The Runner SHALL partition pending runtime events into logical delivery sequences and SHALL schedule each sequence independently. A sequence SHALL preserve the logical AgentSession or Workflow target, its producer family, and the physical runtime identity required by the Server. At most one head attempt per sequence SHALL be active at a time, while a bounded number of independent sequences SHALL be eligible concurrently. A stalled, rejected, or unmatched sequence MUST NOT prevent an unrelated sequence from progressing.

#### Scenario: One blocked Workflow sequence does not block another
- **WHEN** the head of one Workflow sequence times out or receives an unmatched receipt while another Workflow target has a deliverable head
- **THEN** the blocked sequence SHALL remain pending in its own logical group
- **AND** the unrelated sequence SHALL be attempted and SHALL be eligible to settle
- **AND** the scheduler MUST NOT serialize both sequences behind the blocked head

#### Scenario: Retried groups receive bounded fair progress
- **WHEN** several sequence groups fail repeatedly and another group becomes deliverable
- **THEN** retry scheduling SHALL continue to select the deliverable group within the configured concurrency bound
- **AND** repeated failure of one group MUST NOT starve the other group

### Requirement: Each logical sequence preserves FIFO order
The outbox SHALL assign a monotonic sequence position at admission and SHALL deliver records in that order within each logical sequence. A `session.input` or other turn boundary SHALL be positively settled before dependent runtime facts can be settled, and terminal `session.activity` SHALL follow every earlier fact from that turn. A later record MUST NOT be removed, acknowledged, or delivered as settled while an earlier record in the same sequence remains pending, including when a batch response acknowledges later positions.

#### Scenario: Unmatched input fences later facts
- **WHEN** a Workflow `session.input` is pending and the Server returns an empty or mismatched receipt while tool, usage, and terminal facts for that turn are also queued
- **THEN** the input SHALL remain the sequence head
- **AND** the later facts SHALL remain pending behind it
- **AND** no later fact SHALL be treated as settled before the matching input receipt arrives

#### Scenario: A batch cannot skip its first unmatched record
- **WHEN** one delivery batch contains multiple records and the response matches a later record but not the first record
- **THEN** the outbox SHALL retain the first record and every later record that follows it in the same sequence
- **AND** a retry SHALL begin at the first unmatched record
- **AND** receipts SHALL remain associated with their exact submitted record positions

#### Scenario: A newer physical binding does not overtake an older head
- **WHEN** a logical sequence contains an older pending record for `runtimeSessionId=A` and a later record for `runtimeSessionId=B`
- **THEN** the older record SHALL remain the head until its own receipt policy settles it
- **AND** the newer record MUST NOT be retargeted, delivered as a substitute for the older record, or allowed to overtake it

### Requirement: Receipt matching is identity- and attempt-specific
Every delivery attempt SHALL retain the record ID, logical sequence, event type, target, physical runtime session identity, and applicable AgentSession and Agent turn identity that were submitted. A receipt SHALL settle only the exact pending record whose event type and required identity it acknowledges. An empty, malformed, stale, or mismatched response MUST retain that record and MUST NOT acknowledge another record in the group or another group. Receipt policy exceptions SHALL be explicit for event kinds whose protocol defines successful-response settlement; `session.input` and terminal `session.activity` SHALL always use positive matching settlement.

#### Scenario: A receipt for another turn is rejected
- **WHEN** a response carries a valid runtime-event type but its input delivery, AgentSession, or Agent turn identity belongs to another turn
- **THEN** the response SHALL be classified as a receipt mismatch for the submitted record
- **AND** the submitted record SHALL remain pending with its original identity
- **AND** the receipt MUST NOT settle a later record

#### Scenario: A stale response does not retarget delivery
- **WHEN** the Server reports that the recorded physical binding is stale or returns no matching acceptance for it
- **THEN** the outbox SHALL retry the record against its recorded target and `runtimeSessionId`, subject to its retry policy
- **AND** MUST NOT rewrite the record to the current physical binding
- **AND** MUST NOT fabricate a receipt or terminal state

### Requirement: Delivery leases settle late responses safely
Each in-flight delivery attempt SHALL have a unique attempt or lease identity and a bounded timeout. When a lease expires, the record SHALL remain pending and the sequence SHALL remain ordered. A response that arrives after the timeout SHALL be eligible to settle only the same still-pending record when its attempt identity and receipt policy match; it MUST NOT settle a replacement record, a later sequence position, or an unrelated sequence. A late response for a record already settled SHALL be ignored idempotently.

#### Scenario: Matching response arrives after timeout
- **WHEN** a delivery attempt times out, a retry boundary is crossed, and a late response from the original attempt positively acknowledges the original record
- **THEN** the outbox SHALL settle the original pending record at most once
- **AND** SHALL persist the removal before allowing the next record in that sequence to settle
- **AND** the late response MUST NOT cause a duplicate runtime invocation

#### Scenario: Mismatched late response is ignored for settlement
- **WHEN** a timed-out attempt later returns a receipt for another event type, identity, or sequence
- **THEN** the original record SHALL remain pending
- **AND** the later receipt MUST NOT remove the original record or any later record
- **AND** the next retry SHALL continue from the original sequence head

#### Scenario: Late response races with a successful retry
- **WHEN** a retry for the same logical record is already positively settled before the original attempt's response arrives
- **THEN** the late original response SHALL be ignored without changing the next sequence position
- **AND** the record SHALL remain removed exactly once

### Requirement: Delivery retries do not replay runtime execution
Network retry, lease expiry, receipt mismatch, reconnect, or outbox restart SHALL retry only the durable runtime-event record. These delivery actions MUST NOT invoke the Workflow or follow-up runtime again, create a second logical turn, or alter the already-produced runtime result. A locally admitted input SHALL remain the sole execution admission for that turn until its delivery receipt settles.

#### Scenario: Follow-up upload retries after runtime execution
- **WHEN** a follow-up runtime executes after its input is durably admitted and the input or later facts require multiple delivery attempts
- **THEN** the Runner SHALL retry the pending records using their original identities
- **AND** the follow-up runtime SHALL be invoked exactly once
- **AND** a delivery failure SHALL NOT replace the runtime's result with a fabricated transcript result

#### Scenario: Reconnect resumes independent pending groups
- **WHEN** the Runner reconnects with multiple pending sequences, including one sequence whose previous attempt timed out
- **THEN** reconnect SHALL trigger delivery recovery for every eligible sequence
- **AND** each sequence SHALL resume at its own pending FIFO head
- **AND** one sequence's timeout or late response MUST NOT reset, reorder, or duplicate another sequence
