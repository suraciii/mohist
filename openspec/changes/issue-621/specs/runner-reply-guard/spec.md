### Requirement: Detect unpublished replies only for eligible Slack turns
The Runner SHALL apply the reply guard only to a turn with a valid Slack execution context and reply anchor. The guard SHALL cover both initial Agent work and Slack follow-up turns executed through the Pi or OpenCode runtime. At the terminal point of an eligible turn, the Runner SHALL determine whether the Agent has an accepted reply publication for that turn's reply anchor or dispatch reference.

An accepted publication SHALL mean that the existing Agent reply action has been accepted by the Server-owned Slack reply/outbox boundary. The Runner MUST treat an accepted outbox enqueue as published without waiting for provider delivery confirmation. Runtime final assistant text, tool output, liveness status, or a terminal event alone MUST NOT count as an accepted reply publication.

#### Scenario: Initial Pi Slack work ends without an accepted reply
- **WHEN** an initial AgentJob turn runs through Pi with a valid Slack execution context and reaches a terminal point without an accepted Agent reply publication
- **THEN** the Runner SHALL make the turn eligible for one reply-guard advisory

#### Scenario: Initial OpenCode Slack work ends without an accepted reply
- **WHEN** an initial AgentJob turn runs through OpenCode with a valid Slack execution context and reaches a terminal point without an accepted Agent reply publication
- **THEN** the Runner SHALL make the turn eligible for one reply-guard advisory

#### Scenario: Slack Pi follow-up ends without an accepted reply
- **WHEN** a Slack follow-up turn runs through Pi and reaches a terminal point without an accepted Agent reply publication
- **THEN** the Runner SHALL make the turn eligible for one reply-guard advisory

#### Scenario: Slack OpenCode follow-up ends without an accepted reply
- **WHEN** a Slack follow-up turn runs through OpenCode and reaches a terminal point without an accepted Agent reply publication
- **THEN** the Runner SHALL make the turn eligible for one reply-guard advisory

#### Scenario: An accepted reply is pending provider delivery
- **WHEN** the Agent reply action has been accepted into the Server-owned Slack outbox but the Slack adapter has not delivered the pending outbox entry
- **THEN** the Runner SHALL treat the reply as published and SHALL NOT issue a reply-guard advisory

#### Scenario: Runtime output exists without a reply action
- **WHEN** a Slack-bound turn produces final assistant text, tool output, or terminal runtime facts but the Agent reply action was not accepted
- **THEN** the Runner SHALL treat the reply as unpublished and SHALL evaluate the reply guard

### Requirement: Issue one bounded advisory using the existing reply context
For an eligible terminal turn with no accepted reply publication, the Runner SHALL issue at most one advisory opportunity to the same Agent turn/session within a finite bounded wait. The advisory SHALL reuse the existing Slack execution context, reply anchor, and collaboration instructions already supplied to the Agent. It SHALL tell the Agent to either publish a self-contained reply through the existing Agent reply action or deliberately leave the turn silent.

The Runner MUST NOT invent reply content, select a different Slack destination, expose reply-anchor internals, or turn the advisory into a Server-authored Slack message. Any reply produced after the advisory SHALL remain Agent-owned and SHALL use the existing reply publication path.

#### Scenario: The advisory leads to a self-contained Agent reply
- **WHEN** the Agent has not published a reply at the terminal point and publishes a self-contained conclusion, evidence summary, and next step through the existing reply action during the bounded advisory opportunity
- **THEN** the Server SHALL accept that Agent-authored reply through the existing Slack outbox path and the Runner SHALL NOT synthesize or append another reply

#### Scenario: The Agent deliberately chooses silence
- **WHEN** the Agent has not published a reply at the terminal point and chooses the silence branch after receiving the advisory
- **THEN** the Runner SHALL allow the turn to end without a Slack reply and SHALL treat the silence as a valid outcome rather than a failure

#### Scenario: The advisory uses the supplied reply anchor
- **WHEN** the Agent publishes a reply after the advisory
- **THEN** the reply SHALL target the conversation and thread identified by the existing Slack reply anchor, and the Runner SHALL NOT require the Agent to infer a destination from conversation history

### Requirement: Guard each turn at most once
The Runner SHALL track whether the reply guard has already been evaluated or issued for each eligible turn. Repeated terminal notifications, runtime event reconciliation, duplicate delivery signals, or an unaccepted reply after the advisory MUST NOT cause a second advisory or a retry loop. An accepted reply observed before or during the advisory opportunity SHALL suppress any further advisory for that turn.

#### Scenario: Duplicate terminal signals arrive for one unpublished turn
- **WHEN** the same Slack-bound turn reaches its terminal boundary more than once without an accepted reply publication
- **THEN** the Runner SHALL issue no more than one reply-guard advisory for that turn

#### Scenario: A reply is accepted while the advisory is in flight
- **WHEN** the Agent reply action becomes accepted while the bounded advisory opportunity is active
- **THEN** the Runner SHALL stop pursuing another advisory for that turn and SHALL preserve the single accepted Agent reply

#### Scenario: The advisory completes without a publication
- **WHEN** the one advisory opportunity finishes and no Agent reply has been accepted
- **THEN** the Runner SHALL end guard processing for that turn and SHALL NOT issue another advisory

### Requirement: Preserve the original execution outcome on guard failure or interruption
The reply guard SHALL be best effort and SHALL never change the original execution outcome. The Runner SHALL enforce a finite wait for the advisory. If the advisory times out, fails, cannot run because the runtime is unavailable, or is interrupted, the Runner SHALL retain the original success, failure, cancellation, deadline, or unknown outcome and its associated terminal reporting. The Runner MUST NOT retry the advisory, rerun the original turn, or convert an advisory problem into a new turn failure.

#### Scenario: Advisory times out after an otherwise successful turn
- **WHEN** an eligible Slack turn completes successfully without a reply and the advisory does not finish within its bound
- **THEN** the Runner SHALL report the original successful turn outcome and SHALL perform no advisory retry

#### Scenario: Advisory invocation fails
- **WHEN** the reply-guard advisory cannot be invoked or the runtime returns an advisory failure
- **THEN** the Runner SHALL preserve the original turn outcome and SHALL perform no Server-authored fallback reply or retry

#### Scenario: Advisory runtime is unavailable
- **WHEN** the eligible Slack turn reaches the guard boundary but the runtime needed for the advisory is unavailable
- **THEN** the Runner SHALL preserve the original turn outcome and SHALL close guard processing without retrying the turn

#### Scenario: The turn or advisory is interrupted
- **WHEN** the original Slack-bound turn is cancelled, interrupted, or reaches its deadline before the advisory can complete
- **THEN** the Runner SHALL preserve that original interrupted or deadline outcome and SHALL NOT start a replacement turn or a second advisory

### Requirement: Keep liveness and non-Slack execution independent
The reply guard SHALL use only the existing Slack reply action, reply anchor, outbox, and liveness model. It MUST NOT extract runtime output into a Slack reply, alter terminal liveness closeout, or make a missing Agent reply a Runner execution failure. Turns without a valid Slack execution context SHALL remain unchanged by the guard.

#### Scenario: A Slack turn remains silent after guard processing
- **WHEN** a Slack-bound turn ends without an accepted Agent reply, including after the Agent chooses silence or guard processing is unavailable
- **THEN** the Server SHALL finalize the existing terminal liveness state independently and SHALL enqueue no Server-authored fallback reply

#### Scenario: An Agent reply has already promoted the progress entry
- **WHEN** the existing reply action has accepted an Agent reply and promoted or merged it into the Slack outbox entry for the turn
- **THEN** the Runner SHALL leave that reply and the existing liveness finalization unchanged

#### Scenario: A non-Slack turn reaches a terminal point
- **WHEN** a Pi or OpenCode turn has no valid Slack execution context
- **THEN** the Runner SHALL not issue a reply-guard advisory and SHALL preserve the turn's existing execution and reporting behavior
