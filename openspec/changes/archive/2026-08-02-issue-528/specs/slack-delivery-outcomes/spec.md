### Requirement: Explicit delivery failure retries safely without duplicating

When Slack explicitly rejects an outbound message (the post did not take effect), Mohist SHALL be free to retry that delivery, and the retry SHALL NOT produce a duplicate Slack message because Slack never accepted the original. Retries SHALL be bounded in attempt count and SHALL converge: a delivery that exhausts its retry budget SHALL be dead-lettered rather than retried forever. The retry path SHALL be the only path that re-sends; it SHALL NOT be triggered by an unconfirmed outcome.

#### Scenario: Rejected post is retried without a duplicate
- **WHEN** Slack explicitly rejects an outbound post and Mohist retries the same delivery
- **THEN** at most one Slack message results from the rejected post, because the original was never accepted

#### Scenario: Exhausted retries are dead-lettered, not retried forever
- **WHEN** an outbound delivery has failed explicitly for as many attempts as the configured retry budget
- **THEN** the delivery is dead-lettered with its reason and no further automatic retry is attempted

### Requirement: Delivery uncertain outcomes are visible to operators and owners

Outbox rows held in the Delivery uncertain state — including those surfaced by the claim-timeout and uncertain-timeout sweeps — SHALL be listable and visible from the Connection page, the CLI, and the Owner diagnostic surface, together with the reason they could not be confirmed. Before an operator triggers a manual resend of an uncertain delivery, Mohist SHALL warn that a duplicate reply may appear and SHALL offer the authoritative Mohist execution result for inspection first.

#### Scenario: Uncertain deliveries are listed with their reason
- **WHEN** one or more outbound deliveries are in the Delivery uncertain state for a Connection
- **THEN** the Connection page, CLI, and Owner diagnostic surface list those deliveries and each one's reason

#### Scenario: Manual resend warns of possible duplication
- **WHEN** an operator initiates a manual resend of a Delivery uncertain row
- **THEN** Mohist warns that a duplicate reply may result and offers the authoritative execution result for inspection before the resend is committed

### Requirement: Delivery transitions never reclassify execution results

Retrying an explicitly failed delivery, holding a delivery in Delivery uncertain, or dead-lettering a delivery SHALL NOT change the authoritative result of the AgentJob or AgentTurn it reports. The Server is the sole authority that decides whether work completed, failed, or is unknown; the outbound delivery state machine SHALL advance independently of execution-result authority, and a stuck or dead-lettered reply MUST NOT reclassify a completed job as failed or a failed job as completed.

#### Scenario: Retrying a failed delivery does not fail completed work
- **WHEN** an AgentJob completed successfully and the reply delivery is being retried after an explicit failure
- **THEN** the AgentJob remains successfully completed and only the delivery is advanced

#### Scenario: A dead-lettered uncertain reply does not reclassify the job
- **WHEN** an outbound reply is dead-lettered after its uncertain timeout elapsed
- **THEN** the underlying AgentJob or AgentTurn result is unchanged; only the delivery is marked dead-lettered, and the execution result remains available for the operator to inspect
