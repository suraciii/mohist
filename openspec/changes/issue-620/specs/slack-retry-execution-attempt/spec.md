### Requirement: An accepted Retry SHALL start exactly one fresh execution attempt from the original Slack request context
An accepted Retry SHALL start exactly one fresh execution attempt derived from the original Slack request context that produced the failed attempt. The fresh attempt SHALL reuse the same application launch boundary a CLI or Web surface would call; the retry path MUST NOT introduce a Slack-only dispatch shortcut. A Retry click SHALL never start more than one attempt, regardless of how many times its outcome is delivered.

#### Scenario: One click starts one attempt
- **WHEN** an authorized Retry click is accepted
- **THEN** the Server SHALL start exactly one fresh execution attempt for the original Slack request context
- **AND** no additional attempt SHALL be started by any repeat delivery of that click's outcome

#### Scenario: The retry reuses the shared launch boundary
- **WHEN** the retry dispatches a root re-launch or a follow-up turn
- **THEN** the dispatch SHALL flow through the same launch and follow-up application boundary used by non-Slack surfaces, carrying the retry-keyed idempotency identity
- **AND** no Slack-only execution path SHALL be introduced

### Requirement: A failed root request SHALL re-launch with fresh identities under a retry idempotency key
When the failed attempt is a root request (an initial launch), the accepted retry SHALL re-launch with a new Session, a new initial Input, and a new Turn, committed under a retry-specific idempotency key. The retry identities SHALL be pre-minted for the retry operation and SHALL be distinct from the failed attempt's identities; replays of the same retry under the same key SHALL resolve to the same pre-minted identities.

#### Scenario: A root retry mints fresh identities
- **WHEN** an accepted Retry targets a failed root request
- **THEN** the retry SHALL launch a new Session with a new initial Input and Turn, distinct from the failed attempt's session, input, and turn
- **AND** the launch SHALL be recorded under a retry-specific idempotency key

#### Scenario: A root retry replay resolves to the same launch
- **WHEN** the same accepted root retry is processed again under its retry idempotency key
- **THEN** the launch coordinator SHALL resolve to the same pre-minted session, input, and turn identities rather than minting a second launch
- **AND** a conflicting payload under the same retry key SHALL be rejected as an idempotency conflict

### Requirement: A failed threaded turn SHALL retry as a force-new-turn follow-up in the original Session
When the failed attempt is a threaded follow-up turn, the accepted retry SHALL be admitted into the original Session as a force-new-turn follow-up under the retry idempotency key: it MUST create its own new Turn instead of joining or extending any other turn. The retry MUST NOT be attached to an unrelated queued or executing follow-up, and the session's ordinary follow-up admission and queueing behavior SHALL remain unchanged for ordinary messages.

#### Scenario: The retry admits a new turn in the original session
- **WHEN** an accepted Retry targets a failed threaded turn
- **THEN** the original Session SHALL admit the retry as a follow-up that creates its own new Turn
- **AND** the retry SHALL remain bound to the original Session

#### Scenario: The retry never joins an unrelated queued follow-up
- **WHEN** the original Session has another follow-up queued or executing when the retry is admitted
- **THEN** the force-new-turn retry SHALL NOT join, merge with, or extend that unrelated follow-up's turn
- **AND** the unrelated follow-up SHALL continue its own ordinary admission behavior unchanged

### Requirement: The failed attempt SHALL remain immutable history and the retry SHALL stay context-bound
A retry MUST NOT mutate, reopen, or rewrite the failed attempt's session, input, or turn records; the failed attempt SHALL remain immutable history. The retry SHALL never target a different Connection, workspace, or conversation than the failed attempt's original Slack context.

#### Scenario: The failed history is immutable
- **WHEN** an accepted retry starts its fresh attempt
- **THEN** the failed attempt's session, input, and turn records SHALL remain unchanged as history
- **AND** the fresh attempt SHALL be recorded as new state, never as an edit of the failed attempt

#### Scenario: The retry stays in its original context
- **WHEN** a retry operation resolves its dispatch target
- **THEN** the fresh attempt SHALL execute against the same Connection and conversation as the failed attempt's original Slack request
- **AND** no retry SHALL be dispatched into another Connection or conversation

### Requirement: A durable retry operation SHALL be committed before any dispatch
The Server SHALL persist a durable retry-operation record, keyed by the signed action identity, before any dispatch side effect occurs. The record SHALL retain the action key, the retry dispatch key, the pre-minted attempt identities, the pending or outcome state, and the recovery lease, in a dedicated store with its own database migration. A retry whose operation record cannot be committed MUST NOT dispatch.

#### Scenario: Commit precedes dispatch
- **WHEN** an authorized Retry click passes terminal-state validation
- **THEN** the retry-operation record SHALL be durably committed before the fresh attempt is dispatched
- **AND** a commit failure SHALL abort the retry with no execution side effect

#### Scenario: The operation record retains retry identity
- **WHEN** a retry operation is committed
- **THEN** the record SHALL capture the signed action identity, the retry dispatch key, the pre-minted attempt identities, the pending state, and a recovery lease
- **AND** later reconciliation SHALL be able to resume or settle the operation from that record alone

### Requirement: Retry operations SHALL converge on one attempt across replay, redelivery, failover, concurrency, and restart
Repeated delivery of the same Retry action — including Slack interaction redelivery, concurrent clicks, adapter failover, and a Server crash between operation commit and dispatch — SHALL converge on exactly one fresh attempt. A fixed-key recovery reminder SHALL resume committed-but-pending retry operations. A replayed interaction SHALL re-enter the same retry operation and its recorded outcome instead of returning only a duplicate receipt.

#### Scenario: Concurrent clicks converge on one attempt
- **WHEN** the same Retry action is clicked or delivered concurrently more than once
- **THEN** the retry-operation store SHALL admit exactly one operation keyed by the signed action identity
- **AND** all concurrent deliveries SHALL resolve to that single operation's attempt, with no second attempt

#### Scenario: Slack redelivery and adapter failover stay exactly-once
- **WHEN** Slack redelivers the same interaction or a failover adapter delivers the same click again under a different lease
- **THEN** the duplicate delivery SHALL re-enter the already-committed retry operation
- **AND** the outcome SHALL report the single operation's result rather than starting another attempt

#### Scenario: A crash between commit and dispatch resumes once
- **WHEN** the Server crashes after a retry operation is committed but before its dispatch completes
- **THEN** the fixed-key recovery reminder SHALL resume the committed-but-pending operation on restart
- **AND** resumption SHALL dispatch the pre-minted attempt exactly once, without minting a second attempt

#### Scenario: A replayed interaction re-enters the operation
- **WHEN** an interaction for an already-settled retry operation is replayed
- **THEN** the Server SHALL re-enter the recorded operation and return its recorded outcome
- **AND** the response SHALL not be limited to a duplicate receipt

### Requirement: Authoritative terminal state SHALL be validated before dispatch and outcomes SHALL be reported explicitly
Before dispatching, the Server SHALL validate the failed attempt against authoritative session state: only a still-failed, not-yet-retried, and resolvable target SHALL be accepted. Every retry evaluation SHALL report one explicit result — accepted, already applied, stale, or unavailable — and a target that fails validation MUST NOT dispatch.

#### Scenario: A still-failed un-retried target is accepted
- **WHEN** a validated Retry click targets an attempt whose authoritative terminal state is still failed and no retry has yet been applied
- **THEN** the evaluation SHALL report an accepted result and proceed to commit and dispatch the fresh attempt

#### Scenario: An already-retried target is reported as already applied
- **WHEN** a Retry evaluation targets an attempt for which a retry operation has already been applied
- **THEN** the result SHALL be reported explicitly as already applied
- **AND** no additional attempt SHALL be dispatched

#### Scenario: A no-longer-failed target is reported as stale
- **WHEN** a Retry evaluation targets an attempt whose authoritative state is no longer failed (for example it was later settled or recovered)
- **THEN** the result SHALL be reported explicitly as stale
- **AND** no attempt SHALL be dispatched

#### Scenario: An unresolvable target is reported as unavailable
- **WHEN** a Retry evaluation cannot resolve the failed attempt's session, input, or turn from durable state
- **THEN** the result SHALL be reported explicitly as unavailable
- **AND** no attempt SHALL be dispatched
