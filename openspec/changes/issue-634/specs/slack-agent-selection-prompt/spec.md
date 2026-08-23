### Requirement: An ambiguous multi-Bot message produces one interactive chooser, not a free-text prompt

When a channel message's target Agent is ambiguous — a root message that mentions two or more Mohist Bots bound in the same workspace, or an unmentioned reply in a thread bound to two or more Connections — the race-winning Connection SHALL render exactly one chooser message that offers one interactive selection control per candidate Agent. The chooser SHALL be an interactive Slack selection the user clicks, not free text the user must retype or re-mention. For an ambiguous root message the chooser SHALL be posted at the channel root; for an ambiguous thread reply the chooser SHALL be posted in that same thread. The chooser SHALL be delivered through the posting Connection's outbox user-action delivery.

#### Scenario: A root message mentioning several Bots renders one chooser

- **WHEN** a sender authorized by the mentioned Connections sends a channel root message that mentions two or more Mohist Bots bound in the same workspace
- **THEN** exactly one chooser message is posted at the channel root with one selectable choice per mentioned Bot
- **AND** no plain-text "mention a single Bot" ambiguity prompt is posted

#### Scenario: An ambiguous reply in a multi-bound thread renders the chooser in that thread

- **WHEN** an unmentioned reply arrives in a thread bound to two or more Connections and its sender is authorized by the bound Connections
- **THEN** exactly one chooser message is posted in that same thread
- **AND** it is not posted as a channel root message

#### Scenario: An unambiguous message never renders a chooser

- **WHEN** a channel message mentions exactly one workspace Bot, or a reply lands in a thread bound to exactly one Connection
- **THEN** no chooser is rendered and the message follows the existing single-Agent attribution path

### Requirement: The chooser is claimed once across concurrent Connections, redelivery, and adapter failover

The chooser claim SHALL be once-only per ambiguous message identity (workspace, conversation, message). Slack fans one ambiguous event out to every mentioned App and may redeliver it; concurrent per-Connection ingress calls, Slack event redelivery, and adapter failover SHALL collapse to exactly one chooser. A claim loser SHALL observe the existing claim and post nothing. When the race winner's outbox delivery was not persisted, only that winner MAY retry the delivery under the stable chooser dispatch reference; a caller that observes an existing delivery SHALL NOT enqueue another.

#### Scenario: Fanned-out per-Connection ingress produces one chooser

- **WHEN** every mentioned Connection receives the same ambiguous message concurrently
- **THEN** only the race-winning Connection posts the chooser
- **AND** every other Connection acknowledges the event without posting

#### Scenario: A Slack redelivery does not repeat the chooser

- **WHEN** the same ambiguous message is delivered more than once to any mentioned Connection
- **THEN** no additional chooser is posted for that message identity

#### Scenario: A winner retry after a lost delivery stays idempotent

- **WHEN** the race winner reprocesses the ambiguous message after its outbox delivery was not persisted
- **THEN** the retry reuses the stable chooser dispatch reference and produces at most one delivered chooser
- **AND** a caller that finds the delivery already present posts nothing

### Requirement: The claim durably retains the original message's input facts

The chooser claim SHALL durably retain the ambiguous message's normalized input facts at claim time: the original sender's Slack identity, the task text with Bot mentions removed, attachment metadata, and the thread anchor, alongside the workspace, conversation, and message identity. The retained facts SHALL be sufficient for a later accepted selection to start work from the original message with no resend by the user.

#### Scenario: Retained facts survive to selection time

- **WHEN** a user clicks a chooser choice after the ambiguous message was claimed
- **THEN** the work started by that selection uses the retained sender identity, task text, attachment metadata, and thread anchor of the original message
- **AND** the user is not required to retype or resend the task

#### Scenario: A chooser whose claim lacks facts cannot silently degrade

- **WHEN** an ambiguous message is claimed without its input facts being durably recorded
- **THEN** no execution may be started from that claim
- **AND** the failure is surfaced rather than answered from reconstructed or guessed input

### Requirement: The ambiguous message itself starts no work

The ambiguous message SHALL NOT cause any mentioned Connection to create an AgentJob, AgentSession, SessionInput, or provider inbox entry. Work for that message starts only through an accepted selection on its chooser.

#### Scenario: A multi-Bot mention creates no execution resources at ingress

- **WHEN** a root message mentioning two or more workspace Bots is processed by any mentioned Connection
- **THEN** no AgentJob, AgentSession, SessionInput, or provider inbox entry is created for that message

#### Scenario: An ambiguous multi-bound-thread reply creates no execution resources

- **WHEN** an unmentioned reply in a thread bound to two or more Connections is processed
- **THEN** no AgentJob, AgentSession, SessionInput, or provider inbox entry is created for that reply

### Requirement: Chooser candidates are exactly the mentioned workspace Bots

The chooser's candidate set SHALL be derived by intersecting the message's parsed mentions with the workspace's identity-bound Mohist Bots, deduplicated by Bot user id, so each mentioned Bot renders exactly one labeled choice. Human mentions, unknown senders, and Bots managed by other Mohist Servers SHALL NOT appear as choices.

#### Scenario: Human mentions are never choices

- **WHEN** an ambiguous message also mentions human members
- **THEN** only the mentioned workspace-bound Mohist Bots appear as chooser choices

#### Scenario: Duplicate Bot identities collapse to one choice

- **WHEN** multiple Connections in the workspace are bound to the same Bot user id
- **THEN** that Bot appears at most once in the chooser and the message is not treated as more ambiguous because of the duplicates

### Requirement: Unauthorized senders keep the existing owner-only guidance

An ambiguous message from a sender who is not authorized under the applicable Connection access policies SHALL produce the existing non-owner guidance message, unchanged in content and once-only delivery, and SHALL NOT offer that sender a chooser. No work SHALL start from such a message.

#### Scenario: A non-Owner multi-Bot mention receives the existing guidance

- **WHEN** a sender who is not the Owner of the applicable Connections sends a message mentioning several Bots
- **THEN** the existing owner-only guidance message is posted once
- **AND** no chooser is rendered and no work is started for that message
