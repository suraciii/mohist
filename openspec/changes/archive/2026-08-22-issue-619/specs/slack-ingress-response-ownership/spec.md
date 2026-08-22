### Requirement: Server ingress outcomes SHALL identify response ownership
The Server-to-adapter ingress result SHALL explicitly identify whether the blocked Slack event is answered by a durable Server delivery intent or by the adapter's direct backpressure fallback. A result that represents a Server-created durable nudge SHALL identify Server as the owner of the user-visible response. A result that represents the legacy no-intent backpressure path SHALL identify the adapter as the owner of the direct response.

#### Scenario: A durable nudge assigns response ownership to Server
- **WHEN** Server persists a setup or unavailability nudge for a blocked Slack event
- **THEN** the ingress result SHALL identify durable Server delivery as the response owner
- **AND** the adapter SHALL have enough information to acknowledge the event without posting a second direct message

#### Scenario: No durable intent assigns response ownership to the adapter
- **WHEN** Server cannot create a durable response intent for the existing direct backpressure fallback
- **THEN** the ingress result SHALL identify the adapter as the response owner
- **AND** the result SHALL carry the safe direct message text or reason needed for the adapter to respond in the originating context

### Requirement: The adapter SHALL honor Server response ownership
The Slack adapter SHALL send a direct user-visible rejection only when the ingress result assigns response ownership to the adapter. When Server owns the response through a durable intent, the adapter SHALL not post a second direct backpressure or setup message. The adapter SHALL acknowledge a definite Server result only after applying this ownership rule.

#### Scenario: Server-owned nudge is not duplicated by the adapter
- **WHEN** the adapter receives an ingress result indicating that Server already created a durable nudge
- **THEN** the adapter SHALL post no direct rejection message
- **AND** the adapter SHALL acknowledge the Slack event once
- **AND** the durable nudge SHALL remain available to the normal outbox delivery path

#### Scenario: Adapter-owned backpressure is posted directly
- **WHEN** the adapter receives an ingress result indicating an adapter-owned backpressure fallback
- **THEN** the adapter SHALL post exactly one direct message to the originating conversation
- **AND** it SHALL preserve the originating thread anchor when one exists
- **AND** it SHALL acknowledge the Slack event after the direct post succeeds

### Requirement: Durable nudge identity SHALL converge across ingress retries
A setup or unavailability nudge created for a Slack event SHALL use a stable deduplication identity derived from the Connection and the Slack event's stable workspace, conversation, and message identity. Replays of the same event SHALL return the existing response ownership and intent rather than creating another intent or changing ownership.

#### Scenario: Ordered Slack redelivery converges on one durable intent
- **WHEN** the same blocked Slack event is delivered more than once in order
- **THEN** every ingress attempt SHALL resolve to the same durable response intent
- **AND** the adapter SHALL not post a direct fallback for any attempt that reports Server-owned delivery

#### Scenario: A lost ingress result is safely retried
- **WHEN** the adapter cannot confirm the result of the first ingress request and Slack redelivers the same event
- **THEN** the second ingress request SHALL recover the previously committed response ownership and durable intent when it exists
- **AND** the event SHALL produce at most one user-visible message

### Requirement: Concurrent ingress SHALL be idempotent at the ownership boundary
Concurrent ingress attempts for one blocked Slack event SHALL be serialized or conflict-resolved at the durable identity boundary so that only one response intent wins. Losing attempts SHALL return the winning response ownership and SHALL not issue a competing direct-send instruction.

#### Scenario: Concurrent durable-nudge creation has one winner
- **WHEN** two or more Server ingress requests concurrently process the same blocked event
- **THEN** at most one durable nudge intent SHALL be created
- **AND** every successful result SHALL identify Server-owned delivery for that intent
- **AND** no adapter invocation SHALL be instructed to send a direct fallback

### Requirement: Uncertain delivery SHALL reconcile the original nudge intent
The durable outbox SHALL retain one stable nudge intent through claim, delivery uncertainty, retry, and reconciliation. The adapter SHALL reconcile an uncertain delivery by stable provider identity or event delivery identity before any resend. Reconciliation SHALL update or settle the original intent and SHALL NOT create a second nudge intent.

#### Scenario: Uncertain nudge delivery is reconciled as delivered
- **WHEN** the adapter loses the provider response after attempting to send a Server-owned nudge
- **THEN** the nudge SHALL enter the existing uncertain-delivery recovery path
- **AND** reconciliation SHALL mark the original intent delivered when the provider message exists
- **AND** no second Slack message SHALL be posted

#### Scenario: Uncertain nudge delivery is retried only after absence is confirmed
- **WHEN** reconciliation confirms that an uncertain nudge was not applied by Slack
- **THEN** the adapter SHALL retry the original durable intent
- **AND** the retry SHALL preserve its stable delivery identity
- **AND** the retry SHALL not create a new intent or direct adapter fallback

### Requirement: Reconciliation SHALL preserve one response owner
Ingress retries, adapter reconnects, outbox sweeps, and delivery reconciliation SHALL preserve the original response owner for a blocked Slack event. A Server-owned durable nudge SHALL never be converted into an adapter-owned direct message merely because delivery is delayed or uncertain. The adapter-owned fallback SHALL remain direct only when no durable delivery intent was created.

#### Scenario: Reconciliation does not switch a Server-owned nudge to direct send
- **WHEN** a Server-owned nudge is pending, claimed, uncertain, or being reconciled
- **THEN** all subsequent ingress and delivery outcomes SHALL continue to identify Server-owned delivery
- **AND** the adapter SHALL not post an additional direct message

#### Scenario: Legacy direct backpressure remains direct
- **WHEN** the Server returns the adapter-owned fallback because no durable delivery intent exists
- **THEN** delivery reconciliation SHALL not invent a durable nudge for that event
- **AND** the adapter SHALL remain solely responsible for the one direct response

### Requirement: Ingress acknowledgment SHALL follow the definite outcome
The adapter SHALL acknowledge a Slack message event only after it receives a definite ingress result and, for an adapter-owned fallback, after the direct response succeeds. If the Server result is unavailable or unknown before a durable response ownership decision is confirmed, the adapter SHALL leave the event unacknowledged so Slack can redeliver it under the same identity.

#### Scenario: Unknown Server result is not acknowledged
- **WHEN** the ingress request fails or returns an unparseable result before response ownership is known
- **THEN** the adapter SHALL not acknowledge the Slack event
- **AND** Slack SHALL be allowed to redeliver the event

#### Scenario: Durable Server-owned result is acknowledged without a direct post
- **WHEN** the adapter receives a valid Server-owned durable-nudge result
- **THEN** the adapter SHALL acknowledge the event without waiting for a second direct rejection post
- **AND** the adapter SHALL rely on durable outbox delivery for the user-visible message

### Requirement: Combined Server ingress and adapter coverage SHALL exercise ownership end to end
The integration test harness SHALL send representative Slack events through the real Server ingress path and the real Node adapter event handler together. Shared JSON fixtures or isolated unit tests SHALL not be treated as a substitute for this boundary test.

#### Scenario: Durable Server ownership crosses the ingress and adapter boundary once
- **WHEN** the harness sends a blocked DM, channel-root, or unbound-thread event through Server ingress and the real adapter handler
- **THEN** Server SHALL commit one durable nudge and return `responseOwner: server`
- **AND** the adapter SHALL acknowledge without a direct rejection post
- **AND** the normal outbox delivery path SHALL produce one user-visible message with no duplicate direct post

#### Scenario: Adapter-owned fallback crosses the ingress and adapter boundary once
- **WHEN** the harness sends a capacity/backpressure event for which Server creates no durable intent through Server ingress and the real adapter handler
- **THEN** Server SHALL return `responseOwner: adapter`
- **AND** the adapter SHALL make exactly one direct post in the originating context and acknowledge only after it succeeds
- **AND** no durable nudge row or competing Server-owned delivery SHALL be created
