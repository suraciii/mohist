### Requirement: Manager replies are authored only through the Slack reply action
The Manager Agent SHALL author conversational content through the existing Slack reply action `mo slack message send`, using the Server-provided reply anchor for the Manager conversation and thread. Server SHALL never derive a Slack reply from Runtime output, assistant text, command results, terminal facts, or a missing reply. For each accepted Manager input, the Slack delivery boundary SHALL permit at most one final text reply for that input, with duplicate sends and redelivery converging on the existing delivery intent.

#### Scenario: An Agent-authored Manager reply uses the supplied anchor
- **WHEN** the Manager Agent has a useful response for an accepted input
- **THEN** it sends that response through the existing Slack reply action to the Server-provided Manager conversation and thread anchor, and the Server persists it as the Manager's reply delivery intent

#### Scenario: Assistant text is not a Manager reply
- **WHEN** a Manager turn completes with `assistantText` or other Runtime output but no reply action
- **THEN** Server creates no Slack reply from that output and does not copy it into a Manager outbox message

#### Scenario: Duplicate reply sends converge
- **WHEN** the same Manager input is redelivered or the Agent repeats its reply action for the same dispatch
- **THEN** the Server reuses or merges the existing final delivery intent and does not append a second final answer for that input

### Requirement: Manager liveness uses reactions and closes one terminal outcome
For every accepted Manager input, Server SHALL project acceptance with the canonical Received reaction and progress with the canonical Working reaction when work is executing or queued. On a successful, failed, cancelled, unknown, or recovered terminal outcome, Server SHALL remove the Working state when present and converge to exactly one terminal reaction for that input. A successful terminal outcome SHALL use the completed reaction; failed, cancelled, or unknown outcomes SHALL use the attention reaction; a recovered execution SHALL close the current turn using the terminal state confirmed for that recovered execution. Liveness projection SHALL be idempotent across duplicate events, restart, and adapter rebinding.

#### Scenario: Accepted Manager input shows reaction-based progress
- **WHEN** a valid Manager DM is durably accepted and its turn is queued or executing
- **THEN** the Manager source message receives the canonical Received reaction and the in-progress state is represented by the canonical Working reaction without a Server-authored acknowledgement message

#### Scenario: Successful completion closes one terminal reaction
- **WHEN** the Manager turn completes successfully
- **THEN** the Working reaction is removed when present and exactly one completed terminal reaction remains for the input

#### Scenario: Failure, cancellation, and unknown outcomes close attention
- **WHEN** the Manager turn ends as failed, cancelled, or unknown
- **THEN** the Working reaction is removed when present and exactly one attention terminal reaction remains, without a Server-authored failure, cancellation, or unknown-result message

#### Scenario: Recovery converges instead of duplicating liveness
- **WHEN** a Manager turn is recovered after restart or runtime loss and terminal delivery is emitted more than once
- **THEN** the recovered turn closes the same liveness projection exactly once according to its confirmed terminal state and does not leave Working open or add another terminal reaction

### Requirement: Missing Agent replies are valid silence
A Manager turn SHALL remain successful liveness-wise when the Agent sends no reply action. Server MUST NOT synthesize an acknowledgement, progress narration, success summary, failure notice, or terminal message merely because a Manager turn ended without a reply. If the Agent needs to communicate a command result, failure reason, or next step, that content SHALL be sent by the Agent through the reply action; liveness reactions SHALL remain separate from conversational authorship.

#### Scenario: A completed silent Manager turn is valid
- **WHEN** a Manager turn completes without an Agent reply action
- **THEN** Server closes the turn's liveness with the completed terminal reaction and sends no conversational message

#### Scenario: A failed silent Manager turn is valid silence
- **WHEN** a Manager turn fails or becomes unknown without an Agent reply action
- **THEN** Server closes liveness with the appropriate attention terminal reaction and sends no synthesized terminal text

#### Scenario: Command results do not become acknowledgements
- **WHEN** an allowlisted Manager command returns a result during a turn and the Agent chooses not to send a reply
- **THEN** the result remains internal execution data, no Server acknowledgement is published, and the turn remains valid silence

### Requirement: Managed Manager and Agent Bot messages cannot become Manager work
The Manager ingress SHALL classify and suppress messages authored by the enrolled Manager Bot or any eligible managed Agent Bot in the same Workspace before human actor authorization, claim handling, Session routing, or durable input admission. A valid managed-Bot event SHALL be acknowledged once with a definite ignored outcome and SHALL create no Manager Inbox entry, SessionInput, AgentJob, Agent Session, follow-up, reply, reaction, progress, or terminal delivery. The receiving App identity alone SHALL not establish managed authorship, and unmatched or conflicting Bot identities SHALL retain non-managed ingress behavior.

#### Scenario: The Manager Bot's own message is ignored
- **WHEN** the enrolled Manager Bot publishes a message that is delivered back to Manager ingress
- **THEN** ingress returns a definite ignored outcome before actor authorization and creates no durable work or Slack outbox side effect

#### Scenario: A managed Agent Bot cannot trigger Manager work
- **WHEN** any eligible managed Agent App Bot publishes a message received by the Manager App
- **THEN** ingress acknowledges and ignores the message without treating its text as a Manager input or creating liveness or reply state

#### Scenario: Managed Bot redelivery remains side-effect free
- **WHEN** the same managed-Bot message identity is delivered repeatedly, including an event with no human Slack sender
- **THEN** every delivery is acknowledged and ignored, and no Inbox, Session, Job, follow-up, reply, reaction, or progress record is created by the first or later delivery

#### Scenario: An unrelated Bot is not suppressed as managed
- **WHEN** a Bot message has an absent, conflicting, or unregistered author identity
- **THEN** it is not attributed to Mohist-managed authorship and follows the existing non-managed ingress validation and authorization behavior
