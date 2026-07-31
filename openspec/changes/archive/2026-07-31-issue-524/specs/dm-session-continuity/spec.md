### Requirement: One current AgentSession per DM conversation

Each Connection's DM conversation SHALL maintain exactly one current AgentSession at any time. An Owner's normal DM message SHALL continue that current AgentSession as a follow-up input and SHALL NOT create a new AgentJob or a new AgentSession. A new AgentSession SHALL be established only when the DM conversation has no current AgentSession, or when the Owner issues an explicit New task operation.

#### Scenario: The first DM establishes the current session

- **WHEN** the Owner sends the first task DM to the Bot and the DM conversation has no current AgentSession
- **THEN** exactly one AgentJob, one AgentSession, one first SessionInput, and one first AgentTurn are created
- **AND** that AgentSession becomes the DM conversation's current session

#### Scenario: A subsequent normal DM continues the current session

- **WHEN** the Owner sends a normal DM after a current AgentSession exists
- **THEN** the message is accepted as a follow-up input into the current AgentSession
- **AND** no new AgentJob or AgentSession is created

#### Scenario: A normal DM after the current Turn ended still continues the session

- **WHEN** the Owner sends a normal DM after the current session's Turn has reached a terminal result but the session is still current
- **THEN** the message is accepted as a follow-up input into the same current AgentSession, starting the next AgentTurn

### Requirement: New task switches the current session without canceling prior work

An explicit New task operation SHALL create a new AgentJob, a new AgentSession, a first SessionInput, and a first AgentTurn, and SHALL designate that new AgentSession as the DM conversation's current session. The New task operation SHALL NOT cancel, stop, or otherwise interrupt work already executing under a prior AgentSession; prior work SHALL continue to its natural terminal result.

#### Scenario: New task creates new work and switches current

- **WHEN** the Owner issues an explicit New task operation while a current AgentSession exists
- **THEN** a new AgentJob and AgentSession are created
- **AND** the new AgentSession becomes the current session for the DM conversation
- **AND** the prior AgentSession is no longer current

#### Scenario: New task does not stop prior running work

- **WHEN** the Owner issues a New task operation while prior work is still executing
- **THEN** the prior work continues to execute and is neither cancelled nor stopped
- **AND** only the current-session designation switches

### Requirement: Late replies from superseded work carry identifiable work identity

When prior work that was superseded by a New task switch reaches a terminal result after the switch, its reply in the DM conversation SHALL carry an identifiable work identity — the Job and/or AgentSession it belongs to — so the Owner can distinguish it from the current session's results. The late reply SHALL NOT be presented as a result of the current session.

#### Scenario: Prior work completes after a switch and its reply is labeled

- **WHEN** prior work completes after a New task switch has changed the current session
- **THEN** the reply posted to the DM conversation identifies which Job or AgentSession the result belongs to
- **AND** the reply is not presented as a result of the current session

#### Scenario: A late failure is distinguishable from current session output

- **WHEN** prior work fails after a New task switch
- **THEN** the failure reply identifies the originating work and is not confused with the current session's result

### Requirement: A DM during Turn execution is accepted and queued

When the Owner sends a DM while the current session's Turn is executing, the message SHALL be accepted as a follow-up input and queued for a subsequent Turn. The Bot SHALL acknowledge the message as accepted and pending, and SHALL NOT represent it as already executing. The message SHALL NOT be rejected solely because a Turn is executing.

#### Scenario: DM during an executing Turn is accepted as pending

- **WHEN** the Owner sends a DM while the current session's Turn is executing
- **THEN** the message is accepted into a queued Turn
- **AND** the Bot replies that the input is accepted and pending, not executing

#### Scenario: A queued message dispatches after the current Turn ends

- **WHEN** a follow-up input is queued while a Turn is executing and that Turn reaches a terminal result
- **THEN** the queued input is dispatched as the next Turn in the same session

### Requirement: A redelivered DM resolves to the same input

A Slack message delivered more than once — including after `mohist-slack` or Server restart — SHALL always resolve to the same SessionInput and SHALL NOT create a second input, a second AgentTurn, or a second AgentJob. This SHALL hold whether the message was routed as a launch or as a follow-up continuation.

#### Scenario: Redelivered follow-up DM resolves to the same input

- **WHEN** a normal DM that was routed as a follow-up is redelivered by Slack
- **THEN** the redelivery resolves to the original SessionInput and no second input or Turn is created

#### Scenario: Restart does not duplicate accepted work

- **WHEN** the adapter or Server restarts after a DM was accepted and the event is redelivered
- **THEN** the redelivery resolves to the original input regardless of whether it was routed as a launch or a follow-up
