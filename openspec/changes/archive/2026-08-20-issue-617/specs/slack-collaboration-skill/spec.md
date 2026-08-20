### Requirement: Server publishes one versioned canonical Slack collaboration Skill
The Server SHALL publish the embedded Slack collaboration Skill with the stable name `mohist-slack-collaboration`, version `1.0.0`, its complete instruction body, and a content digest. The content digest SHALL be the lowercase hexadecimal SHA-256 digest of the exact UTF-8 bytes of the published instruction body. For this change, the exact LF-delimited v1 instruction fixture, including one trailing LF, is recorded in `design.md` and has digest `dedf18a796543ade06a9e0ece00c086577153e1e633f868c099b01cf910d641b`. The Server SHALL pin the version-to-content mapping and SHALL reject embedded bytes that do not match the pinned digest instead of publishing a changed body under version `1.0.0`. The name, version, instructions, and digest SHALL describe one immutable Skill payload for a given published version.

#### Scenario: Resolving the managed Skill returns its identity and integrity data
- **WHEN** the Server resolves the managed Slack collaboration Skill
- **THEN** it SHALL return the name `mohist-slack-collaboration`
- **AND** it SHALL return version `1.0.0`
- **AND** it SHALL return non-empty instructions containing the canonical Slack collaboration rules
- **AND** its content digest SHALL equal the lowercase SHA-256 digest of those exact instructions encoded as UTF-8

#### Scenario: Same-version asset drift is rejected
- **WHEN** the embedded instruction bytes for published version `1.0.0` differ from the pinned version-to-content mapping
- **THEN** the Server Skill catalog SHALL reject resolution
- **AND** it SHALL NOT publish the changed bytes as version `1.0.0`
- **AND** an intentional wording change SHALL require a new published version and digest mapping

### Requirement: The Skill defines the six Slack collaboration rules
The published Skill SHALL instruct the Agent on all six Slack collaboration rules: the Agent is the speaker and sends useful content only through Mohist's supplied send action; empty acknowledgements are prohibited while a direct human question always receives an answer, including an answer that says there is nothing to add; delegated work calls back to the delegator when the result requires notice or action; a reply is self-contained and proportionate with its conclusion, evidence summary, and next step; the Agent uses the supplied reply anchor and never guesses a destination; and execution resumes silently after restart, Session recovery, or context compaction by rebuilding state from durable records and the thread.

#### Scenario: A normal turn with no new information permits silence
- **WHEN** an Agent turn produces no conclusion, needed notice, or actionable next step and the sender has not asked a direct human question
- **THEN** the Skill SHALL direct the Agent to send no Slack message
- **AND** it SHALL prohibit sending an empty acknowledgement such as a bare confirmation
- **AND** reasoning, tool calls, and other intermediate execution output SHALL remain invisible to Slack users

#### Scenario: A direct human question overrides the silence rule
- **WHEN** a human directly asks the Agent a question
- **THEN** the Skill SHALL direct the Agent to answer the question
- **AND** the Agent SHALL answer even when it has no additional information, by explicitly stating that it has nothing to add rather than remaining silent
- **AND** a bare acknowledgement SHALL NOT satisfy the direct-question rule

#### Scenario: Delegated work calls back with a useful result
- **WHEN** delegated work completes and the result needs the delegator to notice it or act on it
- **THEN** the Skill SHALL direct the Agent to send a result that mentions the delegator
- **AND** the Skill SHALL direct the Agent to mention other people only when they need to act or notice the result
- **AND** a narrative reference to a person SHALL NOT require a mention

#### Scenario: A sent result is self-contained and proportionate
- **WHEN** the Agent sends a Slack result
- **THEN** the Skill SHALL direct the Agent to include the conclusion, a proportionate evidence summary, and the next step in that Slack message
- **AND** fine-grained progress SHALL remain in the Web Session timeline rather than being emitted as Slack chatter

### Requirement: Slack replies use the supplied anchor and recover silently
The Skill SHALL direct the Agent to read the conversation and thread destination from Mohist-provided system facts for every reply. It SHALL prohibit selecting a destination from memory, posting to another channel or thread, or echoing internal anchor fields such as connection IDs, Session IDs, tokens, or member IDs. After a restart, Session recovery, or context compaction, the Agent SHALL rebuild state from durable records and the thread and SHALL continue without announcing the interruption or asking how to proceed.

#### Scenario: The Agent replies only to the Server-provided location
- **WHEN** the Agent has content to send for a Slack-origin turn
- **THEN** the Skill SHALL direct it to use the Mohist-provided send action with the supplied reply anchor
- **AND** it SHALL direct it not to infer or substitute a conversation, thread, or message destination
- **AND** internal anchor fields SHALL NOT appear in the Slack reply text

#### Scenario: Recovery continues without an interruption announcement
- **WHEN** a Slack turn resumes after a restart, Session recovery, or context compaction
- **THEN** the Skill SHALL direct the Agent to reconstruct the relevant state from durable records and the thread
- **AND** it SHALL direct the Agent to continue the work silently
- **AND** it SHALL prohibit announcing the interruption or asking the user how to proceed solely because recovery occurred

### Requirement: The Skill remains an instruction contract rather than a delivery guarantee
The Slack collaboration Skill SHALL define Agent behavior without changing Agent capabilities or assigning reply authorship to the Server. A Runtime result, progress event, or missing Agent send action SHALL NOT be interpreted by this change as a Slack reply. This change SHALL NOT add deterministic natural-language question classification, Server-authored missing-reply detection, or fallback response generation.

#### Scenario: Runtime output does not become an invented Slack reply
- **WHEN** a Slack-origin Runtime turn completes without the Agent using the existing Slack send action
- **THEN** the Server SHALL NOT derive or invent a Slack reply from the Runtime output
- **AND** reply authorship SHALL remain with the Agent's existing Slack reply action
- **AND** the execution result SHALL remain separate from reply delivery
