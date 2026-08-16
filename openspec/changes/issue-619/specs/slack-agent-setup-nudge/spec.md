### Requirement: Agent readiness and Connection availability gate new Slack work
The Server SHALL classify the Slack message before admitting work and SHALL evaluate the bound Agent's canonical executability result for every new Slack launch: a DM route classified as `Launch` or `NewTaskLaunch`, or a channel mention at the root or in a thread that has no existing Session binding for this Connection. The Server SHALL treat `not-configured` and `not-executable` as blocked Agent states and preserve their distinct admission codes. Existing DM and bound channel-thread follow-ups SHALL continue through their persisted Session path without either new-launch gate.

For this admission contract, an enabled, non-disabled Connection is unavailable when its `SetupProgress` is not complete, its `ConnectionHealth` is `Unhealthy` or `Degraded` (including service-offline and backpressure reasons), or its `OfflineGapAt` is set. The Connection-unavailable gate SHALL take precedence over Agent executability, preserve the existing `backpressured` admission code for backpressure, and use `connection_unavailable` for other covered unavailable states. `DesiredState == Disabled` is excluded from this gate and SHALL retain its existing audited-discard behavior. The Slack adapter MUST NOT implement a second readiness or availability rule.

#### Scenario: A blocked Agent receives a new DM task
- **WHEN** a valid Slack DM contains a new task and the canonical executability result is `not-configured`
- **THEN** the Server SHALL reject the new launch with the `agent_not_configured` admission result
- **AND** the Server SHALL NOT admit the message as executable Agent work

#### Scenario: A blocked Agent is mentioned at a channel root
- **WHEN** an authorized Slack member mentions the bound Bot in a channel root and the canonical executability result is `not-executable`
- **THEN** the Server SHALL reject the root launch with the `agent_not_executable` admission result
- **AND** the Server SHALL NOT admit the message as executable Agent work

#### Scenario: A first mention in an unbound channel thread is a new launch
- **WHEN** an authorized Slack member first mentions the bound Bot in an existing human discussion whose thread has no Session binding for this Connection and the canonical executability result is `not-configured` or `not-executable`
- **THEN** the Server SHALL apply the same canonical Agent gate before reading launch history or admitting provider inbox, attachment, workspace, or Session work
- **AND** the Server SHALL treat the message as a new launch rather than as an existing-session follow-up
- **AND** the Server SHALL preserve the message's `body.ThreadTs` as the setup-nudge thread target

### Requirement: Blocked launches receive a Server-authored setup or unavailability nudge
For each blocked new Slack launch caused by a canonical Agent gap or a covered non-disabled Connection-unavailable state, the Server SHALL create a user-visible setup/unavailability nudge in the triggering Slack conversation and its applicable thread or root. The nudge SHALL state that the Agent or Slack Connection is not ready to accept the task and SHALL direct the caller toward the responsible owner or operator. The nudge MUST be authored by the Server and MUST NOT be represented as an Agent reply, an accepted task, queued work, or a working status. The nudge SHALL use the same stable per-message delivery identity for either blocking cause.

#### Scenario: The DM caller is told that setup is required
- **WHEN** a new DM task is blocked by a confirmed Agent readiness gap
- **THEN** the caller SHALL receive one Server-authored setup nudge in that DM conversation
- **AND** the nudge SHALL provide a safe setup direction without claiming that execution started

#### Scenario: A channel-root caller is told that setup is required
- **WHEN** a channel-root launch is blocked by a confirmed Agent readiness gap
- **THEN** the Server SHALL post the setup nudge in the originating channel at the root launch location
- **AND** the nudge SHALL NOT impersonate an Agent-generated answer

#### Scenario: A first channel-thread mention is told that setup is required
- **WHEN** a first mention in an unbound channel thread is blocked by a confirmed Agent readiness gap or covered Connection-unavailable state
- **THEN** the Server SHALL post exactly one setup/unavailability nudge in the originating conversation with `ThreadTs` equal to the triggering message's `body.ThreadTs`
- **AND** the nudge SHALL not be posted at the channel root or impersonate an Agent-generated answer

#### Scenario: A non-disabled unavailable Connection is explained safely
- **WHEN** a new DM or channel launch reaches an enabled Connection whose setup is incomplete, health is unhealthy/degraded, or offline gap is present
- **THEN** the Server SHALL return the applicable Connection admission result and create the required setup/unavailability nudge without accepting executable Agent work
- **AND** the caller-visible text SHALL not expose health reasons, credential details, or repair commands

### Requirement: Readiness detail is authorization-scoped and canonical
The existing authorized owner/operator readiness surfaces SHALL expose the canonical blocked state, every canonical readiness gap, its next action, and its repair entry point, while authorized Connection diagnostics SHALL expose the relevant setup, health, offline, and backpressure facts. The Slack setup/unavailability nudge visible to the triggering caller MUST contain only a safe summary and MUST NOT expose gap codes or detailed messages, raw execution configuration, provider or credential failure details, repair paths, or repair commands. The Server SHALL derive the safe nudge summary from the blocking category and fixed safe copy, and SHALL derive privileged Agent detail from the same canonical executability result rather than maintaining Slack-specific readiness rules.

#### Scenario: A privileged operator investigates an incomplete Agent
- **WHEN** the canonical result is `not-configured` with a `model-missing` gap and an Agent settings repair entry point
- **THEN** an authorized owner/operator surface SHALL expose the state, gap, next action, and repair entry point from that result
- **AND** the Slack nudge SHALL NOT expose the `model-missing` detail or the repair command and path

#### Scenario: A privileged operator investigates an execution configuration failure
- **WHEN** the canonical result is `not-executable` with an execution configuration failure gap
- **THEN** an authorized owner/operator surface SHALL expose that canonical gap and its repair guidance
- **AND** the Slack caller SHALL receive only the safe unavailable/setup summary

### Requirement: Setup nudge delivery is deduplicated by Slack message identity
The Server SHALL derive a stable setup-nudge dispatch reference from the Connection and the triggering Slack message identity `(workspace team, conversation, message timestamp)`. Redelivery or concurrent admission of the same Agent-blocked or Connection-unavailable Slack message SHALL resolve to one logical setup-nudge delivery intent and SHALL never enqueue a second guidance delivery for that message. The delivery intent SHALL retain the original conversation and thread target, whether that target is a DM thread, a channel root, or an unbound channel thread, and SHALL use the existing outbox retry, delivery-uncertain, and reconciliation behavior without blindly creating a new intent.

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
A new Slack launch blocked by Agent readiness or covered non-disabled Connection unavailability SHALL create no `AgentJob`, `AgentSession`, `SessionInput`, `AgentTurn`, or queued provider inbox work. The setup/unavailability nudge itself SHALL be the only new user-facing delivery caused by the blocked admission. Existing Sessions and their execution snapshots SHALL remain persisted and unchanged when the current Agent readiness or Connection availability becomes blocked.

#### Scenario: A blocked DM is rejected before admission
- **WHEN** a new DM task reaches admission while the bound Agent is blocked
- **THEN** the counts of AgentJobs, AgentSessions, SessionInputs, and AgentTurns attributable to that message SHALL remain unchanged
- **AND** no provider inbox row representing queued executable work SHALL be created
- **AND** the Server SHALL persist only the deduplicated setup-nudge delivery for the caller

#### Scenario: A blocked root launch does not alter an existing Session
- **WHEN** a new channel-root launch is blocked while another Session for the Agent already exists
- **THEN** the existing Session and its snapshot SHALL remain readable and byte-for-byte unchanged by the nudge path
- **AND** the blocked root message SHALL NOT be attached to that existing Session as a new input or turn

#### Scenario: An unavailable Connection does not admit executable work
- **WHEN** a new DM, channel-root mention, or first mention in an unbound channel thread reaches a covered non-disabled unavailable Connection
- **THEN** the Server SHALL create only the deduplicated setup/unavailability nudge and the existing Connection admission response
- **AND** it SHALL NOT create provider inbox work, attachments, a workspace, an AgentJob, an AgentSession, a SessionInput, an AgentTurn, or liveness status

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
- **AND** the disabled case SHALL remain audited-discarded without a setup nudge, while the non-disabled backpressured case SHALL receive the required setup/unavailability nudge

#### Scenario: An unhealthy or offline Connection is not an Agent gap
- **WHEN** a new launch reaches an enabled Connection with unhealthy service/credential state or a recorded offline gap
- **THEN** the Server SHALL use the Connection-unavailable admission response and create the safe setup/unavailability nudge
- **AND** the canonical Agent readiness detail SHALL remain independent of the Connection health or offline reason
