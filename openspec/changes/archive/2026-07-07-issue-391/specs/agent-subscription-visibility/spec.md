### Requirement: Trigger labels stamped on subscription-launched sessions

When a subscription triggers an Agent launch, the system SHALL stamp the resulting Agent session's metadata with two trigger correlation labels: `mohist.io/trigger/event-id` set to the id of the triggering CloudEvent, and `mohist.io/trigger/subscription-id` set to the id of the matched subscription that fired. The labels SHALL be recorded using the existing generic AgentSession metadata label mechanism and SHALL persist for the lifetime of the session. Sessions launched manually (not by a subscription) SHALL NOT carry these trigger labels.

#### Scenario: Subscription-triggered session carries both trigger labels

- **WHEN** a subscription triggers a launch for a given CloudEvent
- **THEN** the resulting Agent session metadata SHALL include `mohist.io/trigger/event-id` equal to the CloudEvent id
- **AND** SHALL include `mohist.io/trigger/subscription-id` equal to the matched subscription id

#### Scenario: Manually launched sessions carry no trigger labels

- **WHEN** an Agent session is launched via the manual HTTP launch path (no subscription involved)
- **THEN** the session metadata SHALL NOT include the `mohist.io/trigger/event-id` or `mohist.io/trigger/subscription-id` labels

### Requirement: Event-to-Agent query direction

The system SHALL allow a user to determine, for a given CloudEvent (in particular a workflow or issue event), which Agent and which subscription responded to it. The trigger labels on session metadata SHALL make this join possible without additional structured tracking tables.

#### Scenario: From an event id find the responding Agent and subscription

- **WHEN** a user inspects which Agent session was triggered by a specific event id
- **THEN** the system SHALL allow resolving the session(s) whose `mohist.io/trigger/event-id` matches that event id
- **AND** from that session the user SHALL be able to read the owning Agent identity and the `mohist.io/trigger/subscription-id` that fired

#### Scenario: An event that triggered no response is answerable as "none"

- **WHEN** a user asks which Agent responded to an event that matched no active subscription (or matched but was not selected by arbitration)
- **THEN** the system SHALL allow determining that no session carries a `mohist.io/trigger/event-id` for that event id

### Requirement: Agent-session-to-event query direction

The system SHALL allow a user to determine, for a given subscription-triggered Agent session, which CloudEvent and which subscription caused it. The session metadata trigger labels SHALL make this join possible.

#### Scenario: From a session find the triggering event and subscription

- **WHEN** a user inspects a subscription-triggered Agent session
- **THEN** the session metadata SHALL expose `mohist.io/trigger/event-id` and `mohist.io/trigger/subscription-id`
- **AND** the user SHALL be able to read the triggering CloudEvent and the matched subscription from those ids

#### Scenario: Manually launched sessions report no trigger origin

- **WHEN** a user inspects an Agent session that was launched manually
- **THEN** the session SHALL report no trigger event id and no trigger subscription id
- **AND** the system SHALL allow distinguishing subscription-triggered sessions from manually launched ones by the presence of the trigger labels

### Requirement: Visibility is the primary error-prevention mechanism, not strict conflict rejection

The system SHALL NOT enforce that subscription priorities are configured to be mutually non-overlapping, and SHALL NOT reject or block dispatch when multiple subscriptions match the same event. The primary mechanism for users to detect a misconfigured fallback/takeover relationship (for example, a global fallback firing when a specific takeover was intended) SHALL be the bidirectional visibility provided by the trigger labels. Configuration correctness SHALL remain the user's responsibility; observability SHALL remain the system's responsibility.

#### Scenario: Misconfigured takeover is observable, not blocked

- **WHEN** a user configured a high-priority takeover subscription but, due to a filter mistake, a lower-priority fallback subscription actually responded to an event
- **THEN** the system SHALL NOT have rejected the configuration at creation time
- **AND** SHALL NOT have blocked dispatch when both matched
- **AND** the user SHALL be able to discover, via the trigger labels, which subscription actually fired
- **AND** the user SHALL be able to correct the configuration (for example adjust priorities or filter)
