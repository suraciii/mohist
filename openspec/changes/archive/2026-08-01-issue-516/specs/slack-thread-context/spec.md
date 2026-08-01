### Requirement: First mention in an existing thread imports bounded thread history as startup context
When a connection receives the first `@Bot` mention for its agent in a Slack thread for which it has no existing session binding, and that thread contains prior messages the Bot is permitted to read, the connection SHALL read the bounded thread history and supply it as first-launch startup context via the Agent API. The mentioning message SHALL be the explicit task text of the launch. A first mention that is the root of a thread with no prior readable messages SHALL supply no startup context.

#### Scenario: First mention in a thread with prior discussion imports history
- **WHEN** the first `@Bot` mention for a connection's agent arrives in a thread that already contains prior readable messages, and the connection has no session binding for that thread
- **THEN** the connection SHALL read the bounded thread history it is permitted to see
- **AND** SHALL supply that history as first-launch startup context via the Agent API
- **AND** SHALL treat the mentioning message's text (minus the bot mention) as the explicit task

#### Scenario: Root mention in a brand-new thread imports nothing
- **WHEN** the first `@Bot` mention is the root message of a thread that has no prior readable messages
- **THEN** the launch SHALL supply no startup context
- **AND** SHALL proceed on the mention task text alone, exactly as a channel root mention does without this capability

#### Scenario: Re-mention in an already-bound thread is a follow-up
- **WHEN** an `@Bot` mention arrives in a thread for which the connection already has a session binding
- **THEN** the connection SHALL treat it as a follow-up to that session
- **AND** SHALL NOT import or re-import thread history

### Requirement: Acceptance reply identifies imported background and truncation status
For a launch that imports thread history, the Slack acceptance reply SHALL identify that prior thread discussion is being used as startup background. When that background was truncated, the acceptance reply SHALL state the truncation explicitly. The acceptance reply SHALL NOT claim a complete background when truncation occurred, and SHALL NOT silently omit that background was imported.

#### Scenario: Reply reports that prior discussion is imported
- **WHEN** a first mention launches an agent and imports thread history as startup background
- **THEN** the Slack acceptance reply SHALL state that prior thread discussion is being used as background

#### Scenario: Reply reports truncation honestly
- **WHEN** an importing launch truncates the thread history to fit the startup-context bound
- **THEN** the acceptance reply SHALL state that the imported background was truncated
- **AND** SHALL NOT present the background as complete

### Requirement: Over-limit thread history truncates oldest-first and is marked to the agent
When the bounded thread history exceeds the startup-context size limit, the connection SHALL drop the oldest messages first and retain the most recent discussion up to the limit. The truncation SHALL be marked explicitly in the startup context handed to the agent. The connection SHALL NOT silently drop history, and SHALL NOT drop newest messages to retain older ones.

#### Scenario: Oldest messages are dropped first
- **WHEN** the bounded thread history exceeds the startup-context size limit
- **THEN** the connection SHALL retain the most recent messages up to the limit
- **AND** SHALL drop the oldest messages beyond it

#### Scenario: Truncation is marked in the agent input
- **WHEN** truncation occurs while importing thread history
- **THEN** the startup context handed to the agent SHALL state that truncation occurred and that the oldest messages were dropped

### Requirement: Empty mention requires a task and creates no work
A first `@Bot` mention in an existing thread that contains no task text and no attachments SHALL NOT create an AgentJob or AgentSession. The connection SHALL reply asking the user to send a task.

#### Scenario: Empty mention is rejected with a task prompt
- **WHEN** a first `@Bot` mention arrives in an existing thread and, after removing the bot mention, contains no task text and carries no attachments
- **THEN** the connection SHALL NOT create an AgentJob or AgentSession
- **AND** SHALL reply asking the user to send a task for the agent to perform

### Requirement: Refuse launch when the bounded range cannot be read completely
If the bounded thread history cannot be read completely because of a Slack permission denial, rate-limiting, or a fetch failure, the connection SHALL refuse the delegation: it SHALL NOT create an AgentJob or AgentSession, SHALL NOT import thread history, and SHALL reply asking the user to re-mention later. The connection SHALL NOT launch on partial history, and SHALL NOT guess or infer content missing from an incomplete read. A deliberate truncation to fit the size limit is not incompleteness and SHALL NOT trigger a refusal.

#### Scenario: Permission denial refuses the launch
- **WHEN** the connection cannot read the bounded thread history because the Bot lacks permission to read some of it
- **THEN** the connection SHALL NOT create an AgentJob or AgentSession
- **AND** SHALL NOT import any thread history
- **AND** SHALL reply asking the user to re-mention later

#### Scenario: Rate-limiting or fetch failure refuses the launch
- **WHEN** reading the bounded thread history fails or is rate-limited so the range cannot be obtained completely
- **THEN** the connection SHALL NOT create an AgentJob or AgentSession
- **AND** SHALL reply asking the user to re-mention later

#### Scenario: Deliberate truncation does not trigger a refusal
- **WHEN** the bounded thread history is read completely within the range the Bot requests, but is then truncated to fit the startup-context size limit
- **THEN** the connection SHALL launch the agent with the truncated, explicitly-marked background
- **AND** SHALL NOT refuse the delegation

### Requirement: Accepted input is immutable against later Slack edits or deletes
After thread history has been imported as startup background and the launch accepted, a later Slack edit or deletion of any message in that history SHALL NOT re-run, undo, or rewrite the accepted input, the launch, or the audit record. The connection SHALL NOT react to message edits or deletions by altering already-accepted state. A user corrects the record by sending a follow-up.

#### Scenario: Edited message does not rewrite accepted input
- **WHEN** a Slack message that was imported as startup background is edited after the launch was accepted
- **THEN** the accepted startup context, the AgentJob, the AgentSession, and the audit record SHALL remain unchanged
- **AND** the connection SHALL NOT re-run or alter the launch

#### Scenario: Deleted message does not remove accepted input
- **WHEN** a Slack message that was imported as startup background is deleted after the launch was accepted
- **THEN** the accepted startup context and audit record SHALL remain unchanged
- **AND** the connection SHALL NOT undo the launch or remove the imported content

#### Scenario: Correction happens via follow-up
- **WHEN** a user wants to correct imported background after the launch was accepted
- **THEN** the connection SHALL accept the correction as an ordinary follow-up input to the session
- **AND** SHALL NOT rewrite the already-accepted startup context
