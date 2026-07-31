### Requirement: A channel root mention launches work bound to the thread

When the Owner sends a channel root message that explicitly mentions the Bot and the message's text, after removing the Bot mention, is non-empty or the message carries a usable attachment, the Connection boundary SHALL invoke the Agent API's idempotent launch with a stable call identity derived from the Slack message identity, producing exactly one AgentJob, one AgentSession, one first SessionInput, and one first AgentTurn. That AgentSession SHALL be bound to the thread rooted at that message (the root message's timestamp is the thread identity). The Connection SHALL NOT create a second launch path, a provider-specific execution surface, or any execution authority outside the Agent API.

#### Scenario: A root mention creates exactly one launch bound to the thread

- **WHEN** the Owner sends a channel root message that mentions the Bot and contains a task
- **THEN** exactly one AgentJob, one AgentSession, one first SessionInput, and one first AgentTurn are created via the Agent API
- **AND** that AgentSession is bound to the thread rooted at the mentioning message

#### Scenario: A bare mention without a task does not create work

- **WHEN** the Owner sends a channel root message whose text is empty or only the Bot mention and there is no usable attachment
- **THEN** no AgentJob, AgentSession, SessionInput, or AgentTurn is created
- **AND** the Bot replies in the channel asking the Owner to supply a task

#### Scenario: A mention of a not-yet-bound Agent in an existing thread launches and binds it

- **WHEN** a channel message in a thread that already binds another Agent explicitly mentions a Bot that has no binding in that thread, and contains a task
- **THEN** a new AgentJob and AgentSession are created for the mentioned Agent via the Agent API
- **AND** that AgentSession is bound to the same thread alongside the existing Agent's binding

### Requirement: A reply in a bound thread continues the AgentSession without re-mention

Once an Agent is bound to a thread, a human reply in that thread SHALL be accepted as a follow-up input into that Agent's bound AgentSession and SHALL NOT create a new AgentJob or a new AgentSession. The reply SHALL NOT be required to repeat the Bot mention to continue the session. This SHALL reuse the same follow-up acceptance and dispatch mechanism the Agent API already provides for other Connection sources.

#### Scenario: A thread reply continues the bound session

- **WHEN** a human sends a reply in a thread that is bound to exactly one Agent and the reply does not mention a different Bot
- **THEN** the reply is accepted as a follow-up input into that Agent's bound AgentSession
- **AND** no new AgentJob or AgentSession is created

#### Scenario: A reply after the current Turn ended still continues the session

- **WHEN** a human sends a reply in a bound thread after the bound session's Turn has reached a terminal result
- **THEN** the reply is accepted as a follow-up input into the same bound AgentSession, starting the next AgentTurn

#### Scenario: A reply during an executing Turn is accepted and queued

- **WHEN** a human sends a reply in a bound thread while the bound session's Turn is executing
- **THEN** the reply is accepted as a queued follow-up input for a subsequent Turn
- **AND** the Bot acknowledges the input as accepted and pending rather than executing

### Requirement: One thread may bind multiple Agents, each with an independent session

A single thread MAY bind more than one Mohist Agent simultaneously. Each bound Agent SHALL own its own AgentSession and its own context, keyed by the thread and the Connection. Binding a new Agent to a thread SHALL NOT switch, replace, merge, or pollute any other Agent's bound session or context in that thread. There SHALL be no single "current session" per thread shared across Agents.

#### Scenario: A second Agent binds independently in the same thread

- **WHEN** a thread is already bound to one Agent and a message mentions a second Agent that is not yet bound there
- **THEN** the second Agent gets its own AgentSession bound to the thread
- **AND** the first Agent's bound session and context are unchanged

#### Scenario: Follow-ups to two Agents in one thread stay isolated

- **WHEN** a thread is bound to two Agents and two subsequent replies each address a different one of them
- **THEN** each reply continues only the addressed Agent's bound session
- **AND** neither session's context is merged into or polluted by the other

### Requirement: Acknowledgement and result for thread-bound work are posted into the thread

For any work launched or continued from a thread, the Bot SHALL post its acceptance acknowledgement and its final result summary into that same thread, not as a new channel root message. The acknowledgement and result SHALL follow the same content rules that apply to DM replies: a user-consumable conclusion, evidence summary, and next step, without forwarding hidden reasoning, raw tool output, or credentials.

#### Scenario: Acceptance is acknowledged in the thread

- **WHEN** a root mention or thread reply is accepted as work
- **THEN** the Bot posts the acceptance acknowledgement (accepted, queued, or an explicit rejection) into the originating thread

#### Scenario: The final result is delivered into the thread

- **WHEN** the AgentTurn for thread-bound work reaches a terminal result
- **THEN** the Bot posts a result summary into the originating thread rather than the channel root

### Requirement: Thread binding survives adapter and Server restart

The thread-to-AgentSession binding SHALL be durably held by the Server as Connection provider infrastructure, not by the `mohist-slack` adapter. After the adapter or the Server restarts, a reply in an already-bound thread SHALL continue the original bound AgentSession and SHALL NOT establish a new session. The adapter SHALL hold no binding state of its own.

#### Scenario: A thread reply after restart continues the original session

- **WHEN** the adapter or Server restarts after a thread was bound to an Agent and a reply arrives in that thread
- **THEN** the reply continues the original bound AgentSession
- **AND** no new AgentJob or AgentSession is created for the binding

### Requirement: A redelivered channel or thread message resolves to the same input

Because Slack delivers events at least once, the same Slack message identity on the channel path SHALL always resolve to the same SessionInput and the same resulting Turn, and MUST NOT create a second AgentJob, a second SessionInput, or a second AgentTurn. This SHALL hold across `mohist-slack` restart, Server restart, and any Slack-initiated redelivery within the platform's retry window, and SHALL hold whether the message was routed as a launch or as a thread follow-up.

#### Scenario: Slack redelivers the same channel event

- **WHEN** Slack delivers the same channel or thread event more than once
- **THEN** every delivery resolves to the same SessionInput and no second job, input, or Turn is created

#### Scenario: Restart does not duplicate accepted channel work

- **WHEN** the adapter or Server restarts after a channel message was accepted and the event is redelivered
- **THEN** the redelivery resolves to the original SessionInput regardless of whether it was routed as a launch or a thread follow-up
