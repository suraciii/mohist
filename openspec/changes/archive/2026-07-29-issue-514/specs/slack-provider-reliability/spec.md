### Requirement: The provider inbox deduplicates by stable Slack message identity and is bounded

Server infrastructure SHALL maintain a provider inbox keyed by a stable Slack message identity. The same identity SHALL be accepted into the inbox at most once; redeliveries of an already-accepted identity SHALL resolve to the existing inbox entry without producing a second accepted event or a second SessionInput. The inbox SHALL have a bounded capacity, and when that capacity is reached Mohist SHALL refuse new ingress with an actionable signal rather than dropping already-accepted events.

#### Scenario: Duplicate ingress resolves to one entry
- **WHEN** the same Slack message identity is delivered to the provider inbox more than once
- **THEN** only one inbox entry exists and no second SessionInput is produced

#### Scenario: Capacity reached rejects new ingress
- **WHEN** ingress arrives while the provider inbox is at capacity
- **THEN** the new ingress is refused with an actionable signal and no already-accepted event is dropped to make room

### Requirement: The outbound outbox is bounded and never silently drops terminal messages

Server infrastructure SHALL maintain a bounded outbound outbox of delivery intents. Replaceable intermediate progress entries (such as queued or executing status updates) MAY be merged into the latest state, but final results, explicit failures, and messages that require user action MUST NOT be silently dropped. When the outbox cannot accept another non-replaceable message, the Connection SHALL enter the Degraded state with a Backpressured reason and SHALL stop accepting new Slack input; already-accepted execution continues to be owned and adjudicated by Mohist.

#### Scenario: Replaceable progress is merged
- **WHEN** a newer executing-status update arrives while an older queued-status update for the same dispatch is still unsent
- **THEN** the older replaceable update is merged into the newer one and only the latest progress is delivered

#### Scenario: Terminal messages survive outbox pressure
- **WHEN** the outbox is full and a final result must be delivered
- **THEN** the result is not silently dropped, and the Connection enters Degraded (Backpressured) and stops accepting new Slack input

#### Scenario: Accepted execution continues under backpressure
- **WHEN** the Connection is Degraded (Backpressured)
- **THEN** already-accepted execution continues to be owned and adjudicated by Mohist and is not cancelled by the backpressure state

### Requirement: Unconfirmed delivery is exposed, never blindly reduplicated

When Slack has not confirmed receipt of an outbound message, Mohist SHALL report Delivery uncertain and SHALL NOT blindly send a duplicate. A human-initiated resend SHALL warn that a duplicate reply may result, and the user SHALL be able to inspect the authoritative execution result in Mohist before resending.

#### Scenario: Unconfirmed delivery is reported, not retried blindly
- **WHEN** Slack has not confirmed receipt of an outbound message
- **THEN** Mohist reports Delivery uncertain and does not automatically send a second copy

#### Scenario: Manual resend warns of possible duplication
- **WHEN** a user triggers a manual resend of an uncertain delivery
- **THEN** the action warns that a duplicate reply may appear and offers the authoritative Mohist result for inspection first

### Requirement: Inbox and outbox guarantees survive adapter and Server restart

The provider inbox and outbound outbox SHALL be durable. After `mohist-slack` restart or Server restart, Mohist SHALL resume from the last confirmed positions: already-accepted inbox events remain accepted, unsent outbox entries remain pending, and no accepted event or terminal message is lost or duplicated by the restart itself.

#### Scenario: Adapter restart resumes delivery
- **WHEN** `mohist-slack` restarts after some outbound messages were confirmed and others were not
- **THEN** confirmed messages are not resent and unconfirmed entries are resumed from the outbox

#### Scenario: Server restart preserves accepted events
- **WHEN** the Server restarts after an inbox event has been accepted
- **THEN** the accepted event remains accepted after restart and a redelivery does not turn it into a second accepted event

### Requirement: Slack delivery state never overrides execution result authority

Slack delivery success or failure SHALL NOT change the result of an AgentJob or AgentTurn. The Server is the sole authority that decides whether work succeeds, fails, or remains unknown; a failed Slack reply MUST NOT reclassify a completed AgentJob as failed, and a successful Slack reply MUST NOT reclassify a failed AgentJob as successful.

#### Scenario: Delivery failure does not fail completed work
- **WHEN** an AgentJob has completed successfully but Slack reply delivery fails
- **THEN** the AgentJob remains successfully completed and the delivery failure is reported separately

#### Scenario: Delivery success does not rescue failed work
- **WHEN** an AgentJob has failed but a Slack reply is delivered successfully
- **THEN** the AgentJob remains failed and the successful delivery does not change its result
