### Requirement: Slack text events carry an authoritative sender classification
The Slack adapter SHALL normalize every Slack text message event into a sender classification of `human`, `bot`, or `unknown` and SHALL preserve that classification in the ingress envelope sent to every Slack ingress target. An event with `bot_id` or the `bot_message` subtype SHALL be classified as `bot` regardless of whether Slack also supplies a user identifier. An event with a user identifier and no Bot markers SHALL be classified as `human`; an event without a user identifier or Bot markers SHALL be classified as `unknown`. A Bot envelope SHALL also preserve optional `senderBotAppId` extracted from Slack Bot author metadata such as `bot_profile.app_id`; it MUST NOT use the outer `api_app_id`, which identifies the receiving App. The Manager ingress envelope SHALL carry the same classification and author metadata as the normalized event; it MUST NOT discard either while reducing the envelope.

#### Scenario: Mohist Manager App Bot event has no human sender
- **WHEN** Socket Mode normalizes a message authored by the Mohist Manager App Bot with `bot_id` or subtype `bot_message`, no `user` field, and Bot author metadata identifying its App
- **THEN** the normalized envelope SHALL contain `senderKind = bot`, `senderSlackUserId = null`, and the author `senderBotAppId`
- **AND** an ingress request for either a Manager target or an Agent Connection target SHALL preserve `senderKind = bot` and `senderBotAppId`

#### Scenario: Mohist Agent App Bot event is distinct from a human event
- **WHEN** Socket Mode receives a message authored by a Mohist Agent App Bot, including a Bot message from an Agent App other than the current Connection's Bot
- **THEN** the normalized envelope SHALL contain `senderKind = bot` and the author App metadata
- **AND** the event SHALL remain Bot-authored even when its text mentions a Mohist Bot or an Agent Connection
- **AND** the Server SHALL be able to match that author App against the workspace-wide Mohist-managed App identity set

#### Scenario: Human sender classification remains explicit
- **WHEN** Socket Mode receives a message with a Slack user identifier and no Bot markers
- **THEN** the normalized envelope SHALL contain `senderKind = human` and the sender identifier

#### Scenario: Unknown sender classification remains explicit
- **WHEN** Socket Mode receives a message with neither a Slack user identifier nor Bot markers
- **THEN** the normalized envelope SHALL contain `senderKind = unknown` and `senderSlackUserId = null`
- **AND** the event SHALL not acquire a managed-Bot author identity merely because the receiving App is Mohist-managed

### Requirement: Bot events are acknowledged as ignored Slack deliveries
For a valid normalized Slack text event whose sender classification is `bot` and whose author identity matches a Mohist-managed Manager App or Agent App in the workspace, the adapter SHALL obtain a definite ignored ingress result and SHALL acknowledge the Socket Mode event. Managed Bot events MUST NOT remain unacknowledged merely because they have no `user` field, and the adapter MUST NOT retry them as malformed human messages. Ignoring a managed Bot event SHALL not produce a user-facing Slack response. A Bot event without a matching managed identity SHALL retain the existing target-specific ingress outcome and is not covered by this new ignored-delivery rule.

#### Scenario: A Bot message without a user field is acknowledged
- **WHEN** a Mohist-managed Bot-authored Socket Mode message has a stable Slack event identity, author identity metadata, and no `user` field
- **THEN** the adapter SHALL submit the Bot-classified envelope to the target ingress boundary
- **AND** after the ingress boundary returns `ignored`, the adapter SHALL acknowledge the Slack event
- **AND** the adapter MUST NOT post a rejection or acknowledgement message back to Slack

#### Scenario: A Bot mention does not trigger Slack retry
- **WHEN** a Mohist Manager App Bot or Mohist Agent App Bot message contains a mention that would otherwise target an Agent or Manager conversation
- **THEN** the event SHALL complete with an ignored result and a successful Socket acknowledgement
- **AND** a Slack retry of the same event MUST NOT be required because of missing human sender identity

#### Scenario: A third-party Bot is not newly ignored
- **WHEN** a Bot-classified message has author metadata that does not match any Mohist-managed App identity in the workspace
- **THEN** the adapter SHALL preserve the `senderKind = bot` envelope and submit it to the target ingress
- **AND** the target SHALL retain its pre-616 Bot behavior rather than applying the managed-Bot ignored rule

### Requirement: Bot admission precedes human identity and ingress authorization
After transport authentication and the current runtime lease have been accepted, both the Agent Connection ingress and the Mohist App Manager ingress SHALL resolve the workspace's Mohist-managed Manager and Agent App identities and evaluate the normalized sender classification before requiring a human Slack sender identifier or invoking ingress-specific authorization and conversation logic. A `bot` event with a valid Slack message identity SHALL be accepted as ignored when its author App or supplied Bot user identity matches that managed set, even when `senderSlackUserId` is absent. Managed-Bot admission SHALL occur before owner or access-policy checks, Manager actor authentication or claim handling, conversation/session lookup, follow-up routing, disabled-Connection auditing, and durable input admission. A `bot` event without a managed-identity match SHALL retain its existing target-specific behavior.

#### Scenario: Manager Bot ingress bypasses the required human sender field
- **WHEN** the Manager ingress receives a valid Mohist-managed Bot-classified direct message with matching author metadata and no `senderSlackUserId`
- **THEN** the ingress SHALL return an ignored result rather than a malformed-request or missing-sender error
- **AND** it MUST NOT authenticate a Manager actor, consume a claim, or invoke Manager conversation processing

#### Scenario: Agent Connection Bot ingress bypasses access and conversation checks
- **WHEN** an Agent Connection receives a valid Mohist-managed Bot-classified channel mention or direct message, with or without a sender identifier
- **THEN** the ingress SHALL return an ignored result before owner or access-policy evaluation and before channel or DM routing
- **AND** it MUST NOT require the sender to be the Connection owner or a current Slack member

#### Scenario: A Bot event is ignored even for a disabled Connection
- **WHEN** a valid Mohist-managed Bot-classified event is received for a disabled Agent Connection
- **THEN** the managed-Bot admission rule SHALL return ignored before disabled-event auditing
- **AND** the event MUST NOT create a disabled-discarded inbox record

### Requirement: Ignored Bot events have no durable work or user-facing side effects
Ignoring a Mohist-managed Bot-authored Slack text event SHALL create no provider inbox entry, SessionInput, AgentJob, Agent Session, Agent follow-up, thread or DM session binding, outbox response, or other durable work admission. The system MUST NOT persist or log the managed Bot message text as work input, and it MUST NOT emit a user-facing Slack acknowledgement or rejection. The Socket protocol acknowledgement required by Slack is not a user-facing response and SHALL remain permitted.

#### Scenario: Manager Bot message creates no work
- **WHEN** a Mohist Manager App Bot message with text, a valid direct-message identity, matching managed author metadata, and no human sender identifier is admitted
- **THEN** the Manager ingress SHALL return ignored
- **AND** no Manager provider inbox entry, SessionInput, AgentJob, Agent Session, follow-up, session mapping, or Manager-owned outbox delivery SHALL be created
- **AND** the message text MUST NOT be stored or logged as Manager work input

#### Scenario: Agent App Bot channel mention creates no work
- **WHEN** a Mohist Agent App Bot message mentions an Agent Connection in a channel, including a message authored by another Mohist-managed Agent App Bot
- **THEN** the Agent Connection ingress SHALL return ignored
- **AND** no provider inbox entry, SessionInput, AgentJob, Agent Session, thread binding, follow-up, or Connection-owned outbox delivery SHALL be created
- **AND** no user-facing Slack response SHALL be emitted

#### Scenario: Bot follow-up cannot re-enter an existing session
- **WHEN** a Mohist-managed Bot-authored message is posted as a follow-up in a thread already bound to an Agent Session or as a DM in a conversation with an existing current session
- **THEN** the event SHALL be ignored before follow-up admission
- **AND** the existing Session, SessionInput history, current-session mapping, and pending outbox state SHALL remain unchanged

### Requirement: Human ingress and unknown-sender behavior remain unchanged
The managed-Bot admission rule SHALL apply only to Slack text events classified as `bot` whose author identity matches a Mohist-managed Manager App or Agent App in the workspace. Human Slack messages SHALL retain the existing target-specific authorization and routing behavior for Manager conversations, Agent Connection DMs, channel mentions, and bound-thread follow-ups. Unknown or non-human events that are not classified as `bot` SHALL retain the existing unknown-sender rejection or ignore behavior and MUST NOT be reclassified as human solely to preserve compatibility. Bot events without a managed-identity match, including non-Mohist-managed third-party Bots, SHALL retain their pre-616 target-specific sender validation, authorization, auditing, or ignore behavior.

#### Scenario: Human Manager conversation retains its existing routing
- **WHEN** an authenticated human Manager actor sends a direct message with `senderKind = human`
- **THEN** the Manager ingress SHALL retain its existing claim, authorization, conversation, SessionInput, and response-delivery behavior
- **AND** the Bot admission rule MUST NOT cause the human message to be ignored

#### Scenario: Human channel mention and bound-thread follow-up retain Agent routing
- **WHEN** an authorized human sends a channel mention to an Agent Connection or a human sends a follow-up in a bound thread
- **THEN** the Agent Connection ingress SHALL retain its existing claim, inbox, SessionInput, AgentJob, Session, follow-up, and outbox routing behavior
- **AND** the message SHALL be routed according to the existing channel or bound-thread rules

#### Scenario: Third-party Bot behavior remains target-specific
- **WHEN** a Bot-classified message has no matching Mohist-managed author App or Bot user identity
- **THEN** the Agent Connection and Manager ingress SHALL retain their respective pre-616 Bot behavior
- **AND** the message SHALL not be newly ignored solely because `senderKind = bot`

#### Scenario: Unknown sender remains non-work input
- **WHEN** an Agent Connection receives a valid Slack event classified as `unknown`, including a DM, channel mention, or bound-thread follow-up with no sender identifier
- **THEN** the ingress SHALL retain its existing ignored or rejected outcome for unknown senders
- **AND** it MUST NOT invoke human authorization or create durable work input
