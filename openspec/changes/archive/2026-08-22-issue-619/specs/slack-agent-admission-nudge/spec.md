### Requirement: New Slack work SHALL be gated by Agent readiness and Connection availability
For an ordinary Slack DM with no established Session, an explicit leading `new task` DM even when a current Session exists, a channel root mention, or the first mention of an Agent in an unbound thread, Server SHALL classify the event before any backpressure short-circuit and SHALL evaluate the canonical Agent readiness/executability state and the Connection's current admission availability before admitting new work. When the Agent is not ready or a non-Disabled Connection is unavailable, Server SHALL block the new work before creating execution state.

#### Scenario: An unconfigured Agent blocks a new DM
- **WHEN** a caller sends a task in a DM that has no established Session and the bound Agent is not ready
- **THEN** Server SHALL refuse new-work admission
- **AND** Server SHALL create no Session, SessionInput, Turn, AgentJob, or pending execution inbox work

#### Scenario: An explicit new-task DM is gated even with an established Session
- **WHEN** a caller sends a DM beginning with the explicit `new task` marker and the DM has a current Session mapping
- **THEN** Server SHALL classify the message as new work rather than as a follow-up
- **AND** Server SHALL apply the new-work readiness and Connection-availability gate
- **AND** a blocked message SHALL create no new execution state or follow-up input
- **AND** a known no-intent capacity fallback, if selected, SHALL still report adapter ownership without bypassing the new-work classification

#### Scenario: An unavailable Connection blocks a channel root mention
- **WHEN** an authorized caller mentions the Bot in a channel root message and the non-Disabled Connection cannot accept new work
- **THEN** Server SHALL refuse new-work admission
- **AND** the message SHALL not start an Agent Session or AgentJob

#### Scenario: The first mention in an unbound thread is gated
- **WHEN** a caller first mentions the Bot in a thread with no Session binding and the Agent is not ready or the non-Disabled Connection is unavailable
- **THEN** Server SHALL refuse the thread launch
- **AND** Server SHALL not create a thread Session, SessionInput, Turn, AgentJob, or pending execution inbox entry

### Requirement: A blocked event SHALL receive one durable setup or unavailability nudge
For a blocked new-work event, Server SHALL persist one durable Slack delivery intent addressed to the originating conversation and the applicable thread or message anchor. The intent SHALL use a stable identity derived from the Connection and the Slack event identity so that the same event cannot create another nudge. If the existing no-intent backpressure fallback is used because a durable intent cannot be created, Server SHALL return that outcome explicitly and SHALL still create no execution work.

#### Scenario: The durable nudge is addressed to the originating Slack context
- **WHEN** a new DM, channel-root mention, or unbound-thread mention is blocked
- **THEN** the durable nudge SHALL target the same Slack conversation
- **AND** the nudge SHALL use the originating thread or message context rather than an unrelated conversation

#### Scenario: Redelivery does not create a second nudge
- **WHEN** Slack redelivers the same blocked event with the same workspace, conversation, and message identity
- **THEN** Server SHALL resolve the request to the existing durable nudge intent
- **AND** the event SHALL produce at most one durable nudge intent

#### Scenario: Concurrent admission attempts do not create duplicate nudges
- **WHEN** concurrent Server ingress requests attempt to admit the same blocked Slack event
- **THEN** exactly one request SHALL establish the durable nudge intent
- **AND** all other requests SHALL converge on that same intent without creating execution state or another nudge

### Requirement: Caller-visible guidance SHALL be safe and actionable
The nudge shown to an ordinary Slack caller SHALL state that the Agent cannot accept the requested work and SHALL provide a safe next step. It SHALL NOT disclose Agent configuration details, credentials, internal errors, or repair commands. The caller-visible text SHALL remain independent of the concrete readiness gap and Connection failure details.

#### Scenario: An ordinary caller sees a safe readiness summary
- **WHEN** a caller is blocked because the Agent needs setup
- **THEN** the caller SHALL receive a generic setup or temporary-unavailability summary and a safe next step
- **AND** the message SHALL contain no credential, internal error, or repair-command detail

#### Scenario: An ordinary caller sees a safe Connection summary
- **WHEN** a caller is blocked because the Connection is unavailable or backpressured
- **THEN** the caller SHALL receive a generic temporary-unavailability summary and a safe retry or contact-owner next step
- **AND** the message SHALL not expose internal health diagnostics or credentials

### Requirement: Authorized diagnostics SHALL retain the concrete readiness and availability facts
Owners and authorized operators SHALL be able to use the existing authorized diagnostic surfaces to inspect the concrete Agent readiness/executability state, every applicable execution gap and its next action, Connection state, health or availability reason, and current Connection next action for a blocked event. The diagnostic route SHALL inject and call `AgentReadinessService` and expose an authorized `AgentExecutability` projection from that result, rather than only the structural `ready`/`needs_setup`/`unknown` projection. The diagnostic surfaces SHALL not be replaced by the safe caller summary.

#### Scenario: An operator can diagnose an Agent readiness block
- **WHEN** an authorized operator inspects a Connection whose new work was blocked by Agent readiness
- **THEN** the diagnostic result SHALL expose the concrete readiness state and its actionable next step
- **AND** those details SHALL remain absent from the ordinary caller nudge

#### Scenario: An operator can diagnose a Connection availability block
- **WHEN** an Owner or authorized operator inspects a Connection whose new work was blocked by Connection availability
- **THEN** the diagnostic result SHALL expose the Connection state and the applicable next action
- **AND** the caller-facing nudge SHALL remain generic

### Requirement: Established follow-ups SHALL preserve Session semantics
An ordinary message routed to an established DM Session or an established channel-thread Session SHALL remain a follow-up to that Session. The explicit leading `new task` DM marker is an exception: it SHALL retain its existing new-work launch meaning even when a current DM Session exists. The new-work readiness gate SHALL NOT create a new Session or setup nudge for an ordinary follow-up merely because the current Agent readiness projection is not ready. Existing follow-up capacity and lifecycle behavior SHALL remain authoritative for ordinary follow-up messages.

#### Scenario: A DM follow-up continues its established Session
- **WHEN** a DM already has a current Session and the caller sends another ordinary message without the explicit `new task` marker
- **THEN** Server SHALL route the message through the existing follow-up path
- **AND** Server SHALL not treat it as a new-work setup nudge solely because the Agent is currently not ready

#### Scenario: A bound thread reply remains a follow-up
- **WHEN** a caller replies in a thread already bound to the Agent Session
- **THEN** Server SHALL preserve the established thread Session and follow-up behavior
- **AND** Server SHALL not launch a second Session for the reply

### Requirement: Disabled, executable, and unknown states SHALL retain their existing admission behavior
A Disabled Connection SHALL use its audited discard semantics and SHALL NOT receive setup guidance. An executable Agent SHALL continue to admit eligible new work. An Agent whose readiness is `unknown` SHALL continue to accept the delegation and allow Runner verification to determine the eventual execution result. The change SHALL NOT alter readiness criteria, perform automatic repair, or create setup guidance for Disabled Connections.

#### Scenario: Disabled ingress is audited and discarded
- **WHEN** a Slack event arrives for a Disabled Connection
- **THEN** Server SHALL acknowledge and audit the discard using the Disabled route
- **AND** Server SHALL create no setup or unavailability nudge, Session, SessionInput, Turn, AgentJob, or pending execution inbox work

#### Scenario: An executable Agent is admitted normally
- **WHEN** an eligible new Slack task targets an executable Agent on an available enabled Connection
- **THEN** Server SHALL preserve the normal new-work admission path
- **AND** the task SHALL be allowed to create its normal Session and execution records

#### Scenario: Unknown readiness remains accepted
- **WHEN** an eligible new Slack task targets an Agent whose readiness is `unknown`
- **THEN** Server SHALL admit the task under the existing unknown-readiness behavior
- **AND** the task SHALL wait for Runner verification rather than receiving a setup nudge
