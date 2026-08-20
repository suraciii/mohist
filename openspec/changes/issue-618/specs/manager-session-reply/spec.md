### Requirement: Durable Manager Agent sessions
Every accepted Slack Manager direct message SHALL create or continue one durable Agent Session for the Manager Agent and the `(Enrollment, workspace, conversation)` origin. The current Session mapping SHALL be persisted before the message is considered dispatched, and a follow-up SHALL target that mapped Session until a new Session is established by recovery or an explicit new-session policy.

#### Scenario: First Manager message creates the durable Session
- **WHEN** an authorized actor sends the first non-claim direct message for a Manager workspace and conversation
- **THEN** the system creates one Manager Agent Session, records the Slack origin and initiating actor on the Session, stores the conversation-to-Session mapping, and dispatches the message as the Session's initial input

#### Scenario: Later Manager message continues the mapped Session
- **WHEN** the same authorized actor sends another direct message in a conversation with a current mapped Session
- **THEN** the system records exactly one follow-up input in that Session and dispatches it through the ordinary Agent Session follow-up path

#### Scenario: Replayed Manager message is idempotent
- **WHEN** Slack replays a Manager message with the same immutable workspace, conversation, and message identity
- **THEN** the inbox and Session input deduplication return the existing acceptance and do not create another Session, input, turn, or management dispatch

#### Scenario: Manager Session runtime is missing during continuation
- **WHEN** a mapped Manager Session cannot be resolved to its runtime Session while a new message is being accepted
- **THEN** the system establishes a replacement durable Session for the current origin, updates the conversation mapping, and accepts the current message exactly once without losing it

### Requirement: Natural-language Manager Agent turns
Manager Agent inputs SHALL be ordinary natural-language Agent turns. The built-in Manager instructions SHALL describe ordinary Agent behavior and SHALL NOT instruct the model to emit a private management JSON envelope. The Server MUST NOT require, parse, validate, or execute a private model-output envelope, and MUST NOT generate a Manager-specific tool-result follow-up or synthesized acknowledgement text.

#### Scenario: Manager instructions load
- **WHEN** the built-in Manager Agent starts a turn
- **THEN** its instructions require ordinary natural-language Agent behavior and do not contain the `mohistManagerTool` envelope or a Manager-specific tool-result protocol

#### Scenario: Agent output is ordinary prose
- **WHEN** a Manager Agent turn completes with natural-language output
- **THEN** the Server treats the output as the Agent turn result and performs no Manager JSON-envelope parsing or Manager-specific follow-up dispatch

#### Scenario: Agent output resembles the retired protocol
- **WHEN** a Manager Agent turn contains a `mohistManagerTool`-shaped JSON object, malformed JSON, or any other arbitrary assistant text
- **THEN** the Server does not interpret that text as a management request, does not execute a separate Server-side Manager tool path, and does not enqueue a `managerToolResult` follow-up

#### Scenario: Manager ingress accepts a normal request
- **WHEN** an authorized Manager direct message contains a normal user request
- **THEN** the message is placed in the durable Agent Session without a `Manager request accepted` or equivalent synthesized reply being authored by the Server

### Requirement: Shared Slack Agent execution and reply ownership
Each Manager Agent launch and follow-up SHALL use the ordinary Slack Agent execution contract, including the authoritative Slack reply anchor and the pinned Slack collaboration Skill. The Agent's `mo slack message send` reply action SHALL be the sole source of user-visible Manager reply text; the Server MUST NOT extract assistant text or terminal output to author a reply.

#### Scenario: Initial Manager execution receives the authoritative anchor
- **WHEN** the first Manager message is dispatched
- **THEN** the execution context contains the immutable workspace, conversation, thread root, triggering message, initiating actor, Enrollment or connection identity, Session identity, and dispatch reference needed to target the originating Slack conversation

#### Scenario: Manager Agent sends a reply
- **WHEN** the Manager Agent invokes `mo slack message send` using the supplied reply anchor
- **THEN** the reply is delivered in the originating conversation and thread, uses the shared Slack outbox ownership and deduplication behavior, and no Server terminal handler creates a second reply from the turn output

#### Scenario: Manager Agent has no reply to send
- **WHEN** a Manager Agent turn completes without invoking the reply action
- **THEN** the Server records the execution outcome and liveness state without authoring a fallback, acknowledgement, or terminal text message

### Requirement: Manager message loop prevention
Manager ingress SHALL ignore events authored by the managed Manager bot, and Agent-authored Manager replies SHALL NOT be re-ingested as new Manager user messages.

#### Scenario: Managed bot event is received
- **WHEN** the Manager ingress receives a Slack event classified as authored by the managed bot
- **THEN** the event is acknowledged as ignored and does not create or continue a Manager Session or inbox work item

#### Scenario: Agent reply is projected back through Slack
- **WHEN** a Manager Agent reply is delivered by the Slack adapter
- **THEN** the delivery is excluded from Manager user ingress and cannot start a loopback Manager turn
