### Requirement: Normalize Slack sender kind and author identity
Every Slack text event SHALL be normalized as exactly one sender kind: `human`, `bot`, or `unknown`. An event with Slack Bot markers such as `bot_id` or `subtype=bot_message` SHALL be classified as `bot`; an event with a stable Slack `user` identity and no Bot marker SHALL be classified as `human`; an event with neither a usable human identity nor a Bot marker SHALL be classified as `unknown`. For a Bot event, the adapter SHALL preserve any Slack-provided author Bot identity metadata, including the author Bot App identity when present, separately from the identity of the App receiving the Socket event.

#### Scenario: Human event is normalized with its sender identity
- **WHEN** a Slack text event contains a stable `user` identity and no Bot marker
- **THEN** the normalized event has sender kind `human` and retains that Slack user identity

#### Scenario: Bot event is normalized without requiring a human user
- **WHEN** a Slack text event contains a Bot marker and does not contain a `user` field
- **THEN** the normalized event has sender kind `bot`, has a null human sender identity, and remains a valid normalized event

#### Scenario: Bot author metadata is distinct from the receiving App identity
- **WHEN** a Socket App receives a Bot-authored event that includes an author Bot App identity
- **THEN** the normalized event preserves the author identity as Bot metadata and does not use the event's receiving `api_app_id` as the author identity

#### Scenario: Event without a usable sender is classified as unknown
- **WHEN** a Slack text event contains neither a human sender identity nor a Bot marker
- **THEN** the normalized event has sender kind `unknown` and does not invent an author identity

### Requirement: Preserve sender metadata through both ingress transports
The Slack adapter SHALL forward sender kind, human sender identity, and optional Bot author identity without loss for both Agent Connection ingress and Mohist App Manager ingress. Manager ingress requests SHALL permit the human sender identity to be absent when the normalized event is a Bot event. The transport SHALL keep the receiving Socket App identity and the author Bot identity as separate values.

#### Scenario: Agent Connection transport forwards a Bot author
- **WHEN** a normalized Bot event is sent to an Agent Connection ingress endpoint
- **THEN** the Server receives the `bot` sender kind and the available author Bot identity metadata, including a null human sender when Slack supplied none

#### Scenario: Manager transport forwards an author different from the receiving App
- **WHEN** the Mohist Manager App receives a message authored by a managed Agent App Bot
- **THEN** the Manager ingress request contains the Manager receiving App identity and the Agent App author identity as distinct values

#### Scenario: Missing human sender is not replaced with a synthetic user
- **WHEN** a Bot event has no Slack `user` field and is forwarded to Manager ingress
- **THEN** the transport sends a missing or null human sender identity and the Server can evaluate Bot admission without a fabricated user ID

### Requirement: Attribute managed Bots from registered Mohist identities
The Server SHALL identify a Bot event as a managed Bot only when its preserved author identity matches a registered Mohist-managed Manager App Bot or Agent App Bot in the same Slack workspace. The match SHALL include the Mohist Manager App's own Bot and every managed Agent App Bot, including an Agent App other than the ingress target. A Bot event with no author identity or with an author identity that is not registered for the workspace SHALL not be treated as a managed Bot. The receiving Socket App identity alone SHALL never establish managed authorship.

#### Scenario: Mohist Manager App self-message is managed
- **WHEN** the Manager ingress receives a Bot event whose author identity matches the enrolled Mohist Manager App Bot
- **THEN** the Server classifies the event as a managed Bot

#### Scenario: Mohist Agent App message is managed at another target
- **WHEN** an Agent Connection or the Mohist Manager ingress receives a Bot event whose author identity matches any other managed Agent App Bot in that workspace
- **THEN** the Server classifies the event as a managed Bot and does not require the author to be the receiving target's Bot

#### Scenario: Third-party Bot is not attributed to Mohist
- **WHEN** a Bot event's author identity does not match the enrolled Manager App or any managed Agent App in the workspace
- **THEN** the Server does not classify the event as a managed Bot and applies the existing target-specific handling for an unrelated third-party Bot

#### Scenario: Receiving App identity is insufficient for attribution
- **WHEN** the receiving Socket App's identity matches a Mohist App but the preserved author identity is absent or belongs to an unrelated Bot
- **THEN** the Server does not classify the event as a managed Bot solely because the event was received by a Mohist App

### Requirement: Evaluate managed-Bot admission before human and ingress-specific processing
Both Agent Connection ingress and Mohist App Manager ingress SHALL evaluate managed-Bot admission after stable target and message identity checks but before requiring a human Slack sender identity or invoking ingress-specific authorization, claim handling, access policy, channel or thread routing, conversation processing, or durable input admission. A managed Bot event SHALL not be rejected as malformed merely because it has no `user` field.

#### Scenario: Manager Bot event without a user bypasses sender validation
- **WHEN** Manager ingress receives a valid managed Bot event with no `senderSlackUserId`
- **THEN** it reaches the managed-Bot admission decision and is not rejected by the human-sender-required validation

#### Scenario: Managed Bot does not invoke Manager authorization or conversation logic
- **WHEN** a managed Bot event is sent to Mohist App Manager ingress
- **THEN** Manager claim consumption, actor authentication, actor authorization, and Manager conversation processing are not invoked

#### Scenario: Managed Bot does not invoke Agent Connection routing
- **WHEN** a managed Bot event is sent to an Agent Connection as a DM, channel mention, or bound-thread follow-up
- **THEN** sender access checks, mention and thread routing, session continuation, and Agent launch or follow-up processing are not invoked

#### Scenario: Managed Bot is not audited as disabled input
- **WHEN** a managed Bot event reaches a disabled Agent Connection
- **THEN** managed-Bot admission occurs before disabled-event auditing and the event is not inserted into the disabled-event provider inbox

### Requirement: Acknowledge and ignore managed Bot events
For every valid managed Bot text event received through either ingress target, Server ingress SHALL return a definite ignored outcome and the Socket adapter SHALL acknowledge the Slack event exactly once after receiving that definite outcome. The ignored outcome SHALL not produce a Slack message, reaction, status update, or other user-facing acknowledgement.

#### Scenario: Manager Bot self-message is acknowledged and ignored
- **WHEN** the Mohist Manager Bot publishes a text message that is delivered back to Manager ingress
- **THEN** Manager ingress returns a definite ignored outcome and the adapter acknowledges the Socket event without sending a Slack response

#### Scenario: Agent Bot self-message is acknowledged and ignored
- **WHEN** an Agent App Bot publishes a text message that is delivered back to its own Agent Connection ingress
- **THEN** Connection ingress returns a definite ignored outcome and the adapter acknowledges the Socket event without sending a Slack response

#### Scenario: Managed Agent Bot cannot trigger another Mohist target
- **WHEN** an Agent App Bot publishes a text message that is received by another managed Agent Connection or by the Mohist Manager App
- **THEN** the receiving ingress returns a definite ignored outcome and acknowledges the event without routing it as new work

### Requirement: Ignored managed Bot events have no work side effects
A managed Bot event SHALL create no provider inbox entry, SessionInput, AgentJob, Agent Session, follow-up, conversation-management action, or Slack outbox response. The event text SHALL not be persisted or logged as work input, and the ignored event SHALL not change existing work state or create a user-facing acknowledgement. Repeated delivery of the same managed Bot message identity SHALL remain ignored and SHALL create none of these side effects.

#### Scenario: Manager managed Bot event leaves durable state unchanged
- **WHEN** a managed Bot event is submitted to Manager ingress, including an event with no human sender identity
- **THEN** no Manager provider inbox row, SessionInput, AgentJob, Agent Session, follow-up, or Manager outbox delivery is created

#### Scenario: Connection managed Bot event leaves durable state unchanged
- **WHEN** a managed Bot event is submitted to Agent Connection ingress as a DM, channel mention, or bound-thread reply
- **THEN** no Connection provider inbox row, SessionInput, AgentJob, Agent Session, follow-up, or outbox delivery is created

#### Scenario: Redelivery does not create an ignored-event record or work
- **WHEN** the same managed Bot message identity is delivered more than once
- **THEN** every delivery is acknowledged and ignored, and no durable work-input or response record is created by the first or later delivery

#### Scenario: Ignored text is absent from work-input logging
- **WHEN** a managed Bot event contains message text
- **THEN** any operational record contains only the ignored decision and stable non-content identity, and the message text is not persisted or logged as work input

### Requirement: Preserve existing non-managed Slack ingress behavior
Human messages SHALL retain their existing target-specific authorization and routing behavior for DMs, channel mentions, and bound-thread follow-ups. Unknown sender events SHALL retain their existing target-specific validation or ignore behavior and SHALL not bypass required identity rules. Bot events that are not attributable to a Mohist-managed App SHALL retain the existing handling for unrelated third-party Bots; sender kind alone SHALL not apply the managed-Bot ignore rule.

#### Scenario: Human DM retains normal admission
- **WHEN** a human Slack event with a valid sender identity is delivered to a Connection or Manager target through its supported DM flow
- **THEN** the existing sender authorization, durable admission, and session or conversation behavior is applied unchanged

#### Scenario: Human channel mention and bound follow-up retain routing
- **WHEN** a human event is an explicit Agent mention in a channel or a reply in a bound thread
- **THEN** the existing channel or bound-thread routing and corresponding Agent work behavior are applied unchanged

#### Scenario: Unknown sender retains target-specific behavior
- **WHEN** an event is classified as `unknown` and has no usable human or managed Bot author identity
- **THEN** each ingress target applies its existing unknown-sender validation or ignore behavior, without treating the event as a managed Bot

#### Scenario: Unrelated Bot retains third-party behavior
- **WHEN** a Bot event has an author identity that is not registered with Mohist and is delivered through a supported target flow
- **THEN** the target applies its existing non-managed-Bot validation, authorization, routing, or ignore behavior and does not apply Mohist-managed Bot suppression
