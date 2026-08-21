### Requirement: Manager DMs use the ordinary Agent Session lifecycle
An authorized Manager direct message SHALL be admitted as an ordinary input for the built-in `mohist-slack` Agent Session. The first accepted message for a Manager DM SHALL create one Agent Session, one SessionInput, one AgentTurn, and its associated AgentJob through the normal Agent Session launch boundary. A later message in the same Manager DM SHALL become a SessionInput and SHALL continue the durable Session rather than creating a parallel Manager conversation. Claim and owner-authentication boundary messages SHALL be consumed by ingress and SHALL not be forwarded as Agent work.

#### Scenario: The first authorized Manager DM starts one ordinary Session
- **WHEN** an authenticated Manager actor sends a non-claim message to a Manager DM with no current Session
- **THEN** the Server creates one `AgentSession`, one initial `SessionInput`, one initial `AgentTurn`, and one associated `AgentJob`, and records the message's Slack provenance on those durable records

#### Scenario: A later Manager DM continues the mapped Session
- **WHEN** the same authenticated actor sends a later message to the same Manager DM after the initial turn
- **THEN** the Server records one new `SessionInput` and queues or joins the ordinary follow-up turn for the existing Session without creating a second Manager Session

#### Scenario: Duplicate Manager delivery is idempotent
- **WHEN** the same Manager message identity is delivered again
- **THEN** the Server acknowledges the existing durable admission or duplicate outcome without creating another `SessionInput`, `AgentTurn`, `AgentJob`, or liveness projection

### Requirement: Manager execution carries durable Slack origin and normal Slack context
Every Manager initial launch and follow-up turn SHALL carry an explicit Slack execution source and a complete Server-created Slack execution context. The durable origin SHALL bind the execution to the Manager workspace, Manager enrollment, conversation, authenticated initiating member, Session, triggering message, and dispatch operation. The context SHALL provide the same reply-anchor fields and the same versioned Slack collaboration Skill used for an ordinary Slack Connection. A Manager DM without a Slack context SHALL fail closed rather than execute as non-Slack work.

#### Scenario: Initial Manager launch has a complete Slack origin
- **WHEN** an authorized Manager DM starts a Session
- **THEN** the AgentJob dispatch declares a Slack execution source and carries a reply anchor identifying the Manager workspace, DM conversation, initial message, initiating member, Manager enrollment, Session, and dispatch reference together with the published Slack collaboration Skill

#### Scenario: Manager follow-up preserves the durable DM root
- **WHEN** a follow-up message is dispatched for an existing Manager DM Session
- **THEN** the follow-up identifies its own triggering message while preserving the initial DM message as the durable thread root and carries the same Slack collaboration Skill identity, version, instructions, and digest as the initial launch

#### Scenario: Manager context cannot fall through to ordinary work
- **WHEN** a Manager-origin initial launch or follow-up lacks its required Slack context or anchor
- **THEN** the dispatch is rejected before Runtime invocation or local follow-up enqueue and is not relabeled as non-Slack execution

### Requirement: Manager Session recovery preserves origin and continuity
Manager Session recovery SHALL resolve its workspace, enrollment, actor, conversation, and bound thread root from durable Slack origin facts. Restart, runtime rebinding, adapter lease renewal, or AgentJob recovery SHALL not change the Manager conversation, create duplicate accepted work, or require a new natural-language management protocol. Each recovered execution SHALL continue the canonical Session with its own valid dispatch identity and Slack anchor.

#### Scenario: Server restart resumes an accepted Manager turn
- **WHEN** the Server restarts after a Manager input has been durably accepted but before its execution or terminal delivery completes
- **THEN** recovery resumes or reconciles the existing Session and turn from durable facts, preserves the original Manager conversation and root, and does not create a duplicate input or Session

#### Scenario: Manager adapter rebinding preserves the DM mapping
- **WHEN** the Manager Socket adapter reacquires its lease or reconnects after an interruption
- **THEN** a redelivered or new message resolves through the durable Manager enrollment and DM mapping, and accepted work remains attached to the existing canonical Session

#### Scenario: Runtime recovery receives a fresh Manager execution context
- **WHEN** a Manager AgentJob or follow-up is recovered onto a new execution attempt
- **THEN** the recovered attempt carries the durable Manager Slack origin and a fresh dispatch reference and does not reuse a stale execution credential or stale reply destination

### Requirement: Server does not interpret Manager model output as a management protocol
Server SHALL treat Manager Runtime output as ordinary Agent execution data. It MUST NOT parse assistant text for a private JSON management envelope, execute a management operation because text resembles that envelope, synthesize a `Task: Follow-up` or tool-result SessionInput, or use assistant text as a Slack reply. Management effects SHALL come only from the Manager command capability, and conversational replies SHALL come only from the Agent reply action.

#### Scenario: Legacy management JSON is ordinary Agent output
- **WHEN** a Manager turn emits text containing a `mohistManagerTool` object or any other legacy management envelope
- **THEN** the Server records or observes that text only as Runtime output, performs no management operation, creates no protocol follow-up input, and creates no reply from the text

#### Scenario: A completed Manager turn with prose does not create a reply
- **WHEN** a Manager turn completes with assistant prose but the Agent did not use the Slack reply action
- **THEN** the Server does not publish the prose, an acknowledgement, or a terminal summary to Slack, and the turn remains valid silence

#### Scenario: A command call supplies the authoritative result
- **WHEN** the Manager Agent invokes an allowlisted management command during a turn
- **THEN** the command result is returned through the ordinary execution path for that turn, and the Server does not require a model-output envelope or synthesize another turn to expose it
