### Requirement: A channel-originated stop or cancel is permitted only to the Owner or the session initiator

The Connection boundary SHALL provide a way to stop or cancel an active AgentTurn from a channel. Stopping or cancelling an AgentTurn that was invoked from a channel SHALL be permitted only when the requesting sender is the Connection Owner or the Slack member who initiated that AgentSession. A stop or cancel from any other member SHALL be rejected and SHALL NOT interrupt the Turn.

#### Scenario: The Owner stops a channel-originated Turn

- **WHEN** the Connection Owner issues a stop or cancel for an active Turn that a channel member initiated
- **THEN** the stop or cancel is honored

#### Scenario: The session initiator stops their own Turn

- **WHEN** the Slack member who initiated an AgentSession from a channel issues a stop or cancel for that session's active Turn
- **THEN** the stop or cancel is honored

#### Scenario: Another allowed member cannot stop someone else's Turn

- **WHEN** a member who is authorized to invoke the Agent but did not initiate the session issues a stop or cancel for that session's active Turn
- **THEN** the stop or cancel is rejected and the Turn is not interrupted

### Requirement: Continuing a conversation is distinct from stopping another member's Turn

A member who is authorized to invoke the Agent MAY continue a session with a follow-up input, but SHALL NOT stop or cancel a Turn that another member initiated. Only the Turn's initiator or the Owner may stop or cancel that Turn.

#### Scenario: An allowed member may follow up but not stop another's Turn

- **WHEN** a member who is authorized under the policy sends a follow-up in a thread bound to a session another member initiated
- **THEN** the follow-up is accepted
- **AND** that member's attempt to stop or cancel the active Turn is rejected

### Requirement: A stop or cancel targets only the active Turn at the time it is processed

A stop or cancel SHALL act only on the active Turn at the moment the request is processed. A stop or cancel gesture that was issued for a Turn that has already ended SHALL NOT stop a later Turn that has since begun.

#### Scenario: An expired stop gesture does not stop a later Turn

- **WHEN** a stop or cancel is processed for a Turn that has already ended and a new Turn has since begun
- **THEN** the later Turn is not stopped

### Requirement: The system can determine which Slack member initiated a channel session from recorded provenance

Every accepted channel input SHALL record the Slack member who initiated it as durable provenance. To authorize a stop or cancel, the system SHALL be able to determine, from the recorded input provenance, which Slack member initiated the AgentSession whose Turn is being controlled.

#### Scenario: The initiator is resolved from recorded input provenance

- **WHEN** a stop or cancel is issued for an active Turn on a channel-originated session
- **THEN** the system determines the session's initiating member from the recorded input provenance and authorizes the stop or cancel only when the requesting sender is that initiator or the Owner
