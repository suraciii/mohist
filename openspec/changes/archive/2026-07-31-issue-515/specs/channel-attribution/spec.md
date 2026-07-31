### Requirement: Only DMs, explicit mentions, and bound-thread replies enter Mohist

The Connection boundary SHALL accept for processing only: a direct message, a channel message that explicitly mentions a Bot, or a reply in a thread that is already bound to an Agent. The normalized provider envelope SHALL distinguish a human sender from a Bot sender and an unknown sender. Any other channel message, any message whose sender is a Bot, and any message from an unknown sender SHALL be acknowledged to Slack and ignored before any Job, Session, SessionInput, or inbox entry is created, and the text of such ignored messages SHALL NOT enter Mohist's persistent record or logs.

#### Scenario: A plain channel message without a mention is ignored

- **WHEN** a channel message that does not mention any Bot and is not a reply in a bound thread arrives
- **THEN** no Job, Session, SessionInput, or inbox entry is created
- **AND** the message text is not persisted by Mohist

#### Scenario: A message sent by a Bot is ignored

- **WHEN** a channel message whose sender is a Bot arrives, including the receiving Bot's own messages
- **THEN** Mohist acknowledges and ignores the event without creating a Job, Session, SessionInput, or inbox entry
- **AND** the Bot's message does not become another Agent's input

#### Scenario: A message with no stable sender identity is ignored

- **WHEN** a channel message arrives whose normalized sender kind is unknown because it has no stable Slack user identity
- **THEN** Mohist acknowledges and ignores the event without creating a Job, Session, SessionInput, or inbox entry

#### Scenario: A reply in an unbound thread without a mention is ignored

- **WHEN** a thread reply arrives in a thread that is bound to no Agent and the reply does not mention any Bot
- **THEN** no Job, Session, SessionInput, or inbox entry is created

### Requirement: A channel message is attributed to exactly one Agent when unambiguous

When a channel message addresses exactly one Mohist Agent — a single message mentioning exactly one Bot, or a reply in a thread bound to exactly one Agent that does not mention a different Bot — the Connection boundary SHALL attribute the message to that Agent and route it as a launch or a thread follow-up accordingly. A message that explicitly mentions exactly one Bot that is not yet bound in its thread SHALL be attributed to that Bot and treated as a launch that binds it.

#### Scenario: A single-bot mention is attributed to that Agent

- **WHEN** a channel message mentions exactly one Mohist Bot
- **THEN** the message is attributed to that Bot's Agent and routed as a launch or follow-up for it

#### Scenario: A reply in a single-Agent thread with no conflicting mention continues that Agent

- **WHEN** a reply arrives in a thread bound to exactly one Agent and does not mention a different Bot
- **THEN** the message is attributed to that bound Agent and routed as a thread follow-up

#### Scenario: Another Connection ignores a single-Agent thread reply

- **WHEN** an unmentioned reply in a thread bound only to one Agent is delivered to a different Mohist Connection
- **THEN** that different Connection ignores the event without creating an inbox entry, AgentJob, AgentSession, or SessionInput

### Requirement: Mohist does not guess when the target Agent is ambiguous

When the target Agent cannot be determined unambiguously, the Connection boundary SHALL NOT start, continue, or attribute work to any Agent, and SHALL NOT select one by natural language, channel topic, or the previous speaker. A single channel message that mentions more than one Mohist Bot managed by the same Server SHALL start no work. A reply in a thread bound to more than one Agent that does not explicitly mention one of them SHALL be treated as human discussion and SHALL trigger no Agent. In both cases no Job, Session, SessionInput, or inbox entry SHALL be created.

#### Scenario: A message mentioning multiple Bots starts no work

- **WHEN** a single channel message mentions more than one Mohist Bot managed by the same Server
- **THEN** no Agent is launched or continued and no Job, Session, SessionInput, or inbox entry is created

#### Scenario: A reply in a multi-Agent thread without a mention triggers no Agent

- **WHEN** a reply arrives in a thread bound to more than one Agent and the reply does not mention any one of them
- **THEN** the reply is treated as human discussion and no Agent is triggered
- **AND** no Job, Session, SessionInput, or inbox entry is created

#### Scenario: A multi-Agent thread reply that mentions one Agent continues only that one

- **WHEN** a reply arrives in a thread bound to more than one Agent and explicitly mentions exactly one of the bound Bots
- **THEN** only the mentioned Agent's session is continued and the others are untouched

### Requirement: The choose-one prompt is sent at most once per ambiguous message

When an ambiguous message is held back because its target cannot be determined, the Bot SHALL post at most one prompt asking the user to choose a single Agent. For an ambiguous channel root message, the prompt SHALL be posted as a channel root reply; for an ambiguous thread reply, the prompt SHALL be posted in that same thread. A redelivery of the same ambiguous message SHALL NOT produce a second prompt.

#### Scenario: An ambiguous mention prompts the user to choose once

- **WHEN** a channel message mentions multiple Mohist Bots managed by the same Server
- **THEN** the Bot posts exactly one prompt in the originating channel root or thread asking the user to choose a single Agent

#### Scenario: An ambiguous thread reply is prompted in the same thread

- **WHEN** an unmentioned reply arrives in a thread bound to multiple Agents
- **THEN** the Bot posts the choose-one prompt in that same thread and not as a channel root message

#### Scenario: A redelivered ambiguous message does not repeat the prompt

- **WHEN** the same ambiguous message is delivered more than once
- **THEN** no additional choose-one prompt is posted

### Requirement: Channel invocation is restricted to the Connection Owner

For this change, a channel mention or a reply in a bound thread SHALL be accepted only when the sender is the Connection's Owner. A mention or bound-thread reply from a sender who is not the Owner SHALL be rejected with an actionable reason and SHALL create no AgentJob, AgentSession, SessionInput, or inbox entry. Owner-only access for channels SHALL reuse the Connection's bound Owner identity and SHALL NOT introduce a new access-policy model.

#### Scenario: An Owner mention in a channel is accepted

- **WHEN** the Connection Owner sends a channel root message mentioning the Bot with a task
- **THEN** the message is accepted and work is created

#### Scenario: A non-Owner mention in a channel is rejected

- **WHEN** a sender who is not the Connection Owner mentions the Bot in a channel
- **THEN** the message is rejected with an actionable reason
- **AND** no Job, Session, SessionInput, or inbox entry is created

#### Scenario: A non-Owner reply in a bound thread is rejected

- **WHEN** a sender who is not the Connection Owner replies in a thread bound to the Agent
- **THEN** the reply is rejected with an actionable reason and creates no Agent resources

### Requirement: Every accepted channel input records its full provenance

Every channel or thread input that Mohist accepts SHALL durably record the Slack workspace, channel, thread, and member identity it originated from, so any execution can answer which workspace, channel, thread, and member initiated it. These identities are audit facts; they SHALL NOT grant Mohist administrative authority, and SHALL NOT allow a message to switch Project, Agent, or access scope.

#### Scenario: An accepted root mention records workspace, channel, thread, and member

- **WHEN** an Owner's channel root mention is accepted as work
- **THEN** the resulting input records the originating workspace, channel, thread, and member identity

#### Scenario: An accepted thread follow-up records workspace, channel, thread, and member

- **WHEN** a human reply in a bound thread is accepted as a follow-up input
- **THEN** the resulting input records the originating workspace, channel, thread, and member identity
