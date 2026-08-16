### Requirement: Canonical readiness gates new Slack work
The Server SHALL evaluate the bound Agent's canonical executability result when admitting a new Slack DM task or a channel-root Agent mention. The Server SHALL treat `not-configured` and `not-executable` as blocked states, preserve their distinct admission codes, and perform this decision before accepting provider inbox work, preparing a workspace, or launching execution. The Slack adapter MUST NOT implement a second readiness rule.

#### Scenario: A blocked Agent receives a new DM task
- **WHEN** a valid Slack DM contains a new task and the canonical executability result is `not-configured`
- **THEN** the Server SHALL reject the new launch with the `agent_not_configured` admission result
- **AND** the Server SHALL NOT admit the message as executable Agent work

#### Scenario: A blocked Agent is mentioned at a channel root
- **WHEN** an authorized Slack member mentions the bound Bot in a channel root and the canonical executability result is `not-executable`
- **THEN** the Server SHALL reject the root launch with the `agent_not_executable` admission result
- **AND** the Server SHALL NOT admit the message as executable Agent work

### Requirement: Blocked launches receive a Server-authored setup nudge
For each blocked new Slack launch, the Server SHALL create a user-visible setup nudge in the triggering Slack conversation and its applicable thread or root. The nudge SHALL state that the bound Agent is not ready to accept the task and SHALL direct the caller toward Agent setup through the responsible owner or operator. The nudge MUST be authored by the Server and MUST NOT be represented as an Agent reply, an accepted task, queued work, or a working status.

#### Scenario: The DM caller is told that setup is required
- **WHEN** a new DM task is blocked by a confirmed Agent readiness gap
- **THEN** the caller SHALL receive one Server-authored setup nudge in that DM conversation
- **AND** the nudge SHALL provide a safe setup direction without claiming that execution started

#### Scenario: A channel-root caller is told that setup is required
- **WHEN** a channel-root launch is blocked by a confirmed Agent readiness gap
- **THEN** the Server SHALL post the setup nudge in the originating channel at the root launch location
- **AND** the nudge SHALL NOT impersonate an Agent-generated answer

### Requirement: Readiness detail is authorization-scoped and canonical
The existing authorized owner/operator readiness surfaces SHALL expose the canonical blocked state, every canonical readiness gap, its next action, and its repair entry point. The Slack setup nudge visible to the triggering caller MUST contain only a safe summary and MUST NOT expose gap codes or detailed messages, raw execution configuration, provider or credential failure details, repair paths, or repair commands. The Server SHALL derive both the safe summary and privileged detail from the same canonical executability result rather than maintaining Slack-specific readiness rules.

#### Scenario: A privileged operator investigates an incomplete Agent
- **WHEN** the canonical result is `not-configured` with a `model-missing` gap and an Agent settings repair entry point
- **THEN** an authorized owner/operator surface SHALL expose the state, gap, next action, and repair entry point from that result
- **AND** the Slack nudge SHALL NOT expose the `model-missing` detail or the repair command and path

#### Scenario: A privileged operator investigates an execution configuration failure
- **WHEN** the canonical result is `not-executable` with an execution configuration failure gap
- **THEN** an authorized owner/operator surface SHALL expose that canonical gap and its repair guidance
- **AND** the Slack caller SHALL receive only the safe unavailable/setup summary

### Requirement: Setup nudge delivery is deduplicated by Slack message identity
The Server SHALL derive a stable setup-nudge dispatch reference from the Connection and the triggering Slack message identity `(workspace team, conversation, message timestamp)`. Redelivery or concurrent admission of the same Slack message SHALL resolve to one logical setup-nudge delivery intent and SHALL never enqueue a second guidance delivery for that message. The delivery intent SHALL retain the original conversation and thread target and SHALL use the existing outbox retry, delivery-uncertain, and reconciliation behavior without blindly creating a new intent.

#### Scenario: Slack redelivers a blocked DM
- **WHEN** the same blocked DM event is admitted more than once with the same workspace, conversation, and message timestamp
- **THEN** all admissions SHALL resolve to the same setup-nudge delivery intent
- **AND** the outbox SHALL contain at most one setup-nudge delivery for that message

#### Scenario: Two ingress attempts race for a blocked channel root
- **WHEN** concurrent requests report the same blocked channel-root message identity
- **THEN** one request SHALL win creation of the setup-nudge intent
- **AND** the losing request SHALL observe or reuse that intent without producing another Slack message

#### Scenario: Setup-nudge delivery is uncertain
- **WHEN** the provider result for a setup nudge cannot be confirmed
- **THEN** the Server SHALL retain the same stable delivery intent for reconciliation or retry
- **AND** automatic recovery SHALL NOT enqueue a second setup nudge for the triggering message

### Requirement: Blocked admission creates no execution resources
A blocked new Slack launch SHALL create no `AgentJob`, `AgentSession`, `SessionInput`, `AgentTurn`, or queued provider inbox work. The setup nudge itself SHALL be the only new user-facing delivery caused by the blocked admission. Existing Sessions and their execution snapshots SHALL remain persisted and unchanged when the current Agent readiness becomes blocked.

#### Scenario: A blocked DM is rejected before admission
- **WHEN** a new DM task reaches admission while the bound Agent is blocked
- **THEN** the counts of AgentJobs, AgentSessions, SessionInputs, and AgentTurns attributable to that message SHALL remain unchanged
- **AND** no provider inbox row representing queued executable work SHALL be created
- **AND** the Server SHALL persist only the deduplicated setup-nudge delivery for the caller

#### Scenario: A blocked root launch does not alter an existing Session
- **WHEN** a new channel-root launch is blocked while another Session for the Agent already exists
- **THEN** the existing Session and its snapshot SHALL remain readable and byte-for-byte unchanged by the nudge path
- **AND** the blocked root message SHALL NOT be attached to that existing Session as a new input or turn

### Requirement: Executable, unknown, and connection states remain independent
An Agent with canonical executability `executable` SHALL continue through the existing Slack launch and status flow without a setup nudge. An Agent with canonical executability `unknown` SHALL remain admitted for Runner verification without a setup nudge. Connection health, desired Connection state, and Agent readiness SHALL remain separate concerns: a Connection health condition SHALL NOT be reported as an Agent setup gap, and a blocked Agent readiness result SHALL NOT be converted into a Connection lifecycle state.

#### Scenario: An executable Agent accepts a Slack task
- **WHEN** an authorized new DM or channel-root launch targets an Agent with executability `executable`
- **THEN** the Server SHALL accept the task through the existing inbox and launch flow
- **AND** it SHALL create the normal execution resources and SHALL NOT create a setup nudge

#### Scenario: Unknown executability remains admitted
- **WHEN** an authorized new Slack launch targets an Agent with executability `unknown`
- **THEN** the Server SHALL accept the task for Runner verification using the existing unknown-readiness behavior
- **AND** it SHALL NOT emit a blocked-Agent setup nudge

#### Scenario: Connection health is the blocking condition
- **WHEN** the Agent is executable or unknown but the Slack Connection is disabled or backpressured
- **THEN** the Server SHALL use the existing Connection-health admission response
- **AND** it SHALL NOT claim that the Agent has a readiness gap solely because the Connection is unavailable
