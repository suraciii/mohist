### Requirement: Slack turns SHALL receive a versioned collaboration contract
Every Slack root launch and follow-up turn SHALL carry a Server-generated collaboration context for the `mohist-slack-collaboration` Skill. The context SHALL include the supported context version, a Server-selected reply anchor, the Skill name and version, the Skill instructions, and a content hash. The reply anchor SHALL identify the workspace, conversation, thread root, triggering message, initiating member, Connection, Session, and dispatch reference. When a Slack message has no thread root, the triggering message SHALL be used as the thread root.

#### Scenario: A channel thread follow-up is dispatched
- **WHEN** the Server dispatches a follow-up for a Slack message in an existing thread
- **THEN** the context SHALL contain the existing thread root and the current message as the triggering message
- **AND** the context SHALL contain the Server-selected conversation and dispatch reference

#### Scenario: A direct message has no thread root
- **WHEN** the Server dispatches a Slack direct message without a thread root
- **THEN** the context SHALL use the direct message's triggering message as the reply thread root
- **AND** the Agent SHALL receive a complete reply anchor rather than selecting a destination itself

### Requirement: The embedded Skill identity and content SHALL be verifiable at dispatch time
The Server SHALL resolve `mohist-slack-collaboration` from its embedded Skill asset and SHALL publish its versioned identity with the Slack context. The content hash SHALL be the lower-case SHA-256 digest of the Skill instructions encoded as UTF-8. A changed collaboration contract SHALL produce an updated content hash and versioned asset contract. The Runner SHALL accept a Slack context only when its supported context version, required anchor fields, Skill identity, instructions, and content hash are valid and mutually consistent; otherwise it SHALL reject the Slack execution before invoking the Agent runtime.

#### Scenario: The embedded Skill is resolved for a valid Slack dispatch
- **WHEN** the Server creates a Slack execution context
- **THEN** the context SHALL contain the embedded `mohist-slack-collaboration` instructions
- **AND** its content hash SHALL equal the SHA-256 digest of those exact instructions
- **AND** the Runner SHALL inline those instructions as the Slack collaboration Skill

#### Scenario: A Slack context has a tampered Skill body
- **WHEN** the Runner receives a Slack context whose content hash does not match its instructions
- **THEN** the Runner SHALL reject the context as invalid
- **AND** the Agent runtime SHALL NOT be invoked for that dispatch

### Requirement: A direct human question SHALL receive an active, useful answer
The Skill SHALL require the Agent to answer every direct human question through an explicit Slack reply action. This requirement SHALL apply even when the Agent has no additional result to report. The reply SHALL contain useful answer content; an empty message or acknowledgement-only response SHALL NOT satisfy the requirement. When there is genuinely nothing additional to add, the Agent SHALL state that concisely instead of ending the turn silently.

#### Scenario: A direct question has a known answer
- **WHEN** a human asks the Agent a direct question during a Slack turn
- **THEN** the Agent SHALL actively send a reply containing the answer
- **AND** the Agent SHALL NOT end the turn silently

#### Scenario: A direct question has no additional information
- **WHEN** a human asks a direct question and the Agent has no new information beyond the current conversation
- **THEN** the Agent SHALL send a concise answer that states there is nothing additional to add
- **AND** the Agent SHALL NOT send only an acknowledgement such as "got it" or "understood"

### Requirement: Non-informational turns SHALL preserve valid silence without empty acknowledgements
A Slack turn that is not a direct human question SHALL be allowed to end without a reply when it produces no conclusion, result, failure reason, or needed next step. The Agent SHALL NOT send a message whose only content is an acknowledgement. Silence in this case SHALL be treated as a normal completion and SHALL NOT be replaced by a system-generated acknowledgement or summary.

#### Scenario: A human acknowledgement does not require a response
- **WHEN** a Slack turn contains only a non-question acknowledgement and the Agent has no new information to provide
- **THEN** the Agent SHALL send no Slack reply
- **AND** the turn SHALL be allowed to complete normally

#### Scenario: Work produces a result
- **WHEN** a Slack turn produces a conclusion, result, failure reason, or needed next step
- **THEN** the Agent SHALL send that information in a Slack reply
- **AND** the Agent SHALL NOT replace the result with an empty acknowledgement

### Requirement: Slack reply authorship SHALL remain with the Agent and the Server-provided anchor
The Agent SHALL author a Slack response only through the explicit Mohist Slack reply action, using the conversation and thread target from the injected reply anchor. The Agent SHALL NOT infer a destination from memory, target another conversation, or echo internal anchor fields in the reply body. A reply that reports completed work, failure, or required human action SHALL be self-contained and SHALL include the applicable conclusion, evidence summary, and next step. Fine-grained progress SHALL remain in the Web session timeline rather than the Slack reply. When delegated work completes, the result reply SHALL mention the delegator. The Agent SHALL mention a person only when that person needs to act or notice the result; a narrative reference SHALL NOT require a mention.

#### Scenario: A completed turn sends its result
- **WHEN** an Agent turn reaches a conclusion or produces a result
- **THEN** the Agent SHALL send the result through the explicit Slack reply action
- **AND** the action SHALL use the Server-provided conversation and thread-root values
- **AND** the reply SHALL include the conclusion, relevant evidence, and next step without requiring another tool to interpret the outcome

#### Scenario: A turn fails or needs human action
- **WHEN** the Agent cannot complete the requested work or requires a human decision
- **THEN** the Agent SHALL send the concrete failure or required decision and the next action through the explicit Slack reply action
- **AND** the reply SHALL NOT rely on a system-generated template to explain the outcome

#### Scenario: A reply is composed from an internal anchor
- **WHEN** the Agent sends a Slack reply using the injected anchor
- **THEN** the reply destination SHALL be the anchor's Server-selected conversation and thread target
- **AND** the reply body SHALL NOT contain connection identifiers, Session identifiers, tokens, or member identifiers from the anchor

#### Scenario: A result does not require a person to act or notice it
- **WHEN** a result mentions a person only as narrative context and no person needs to act or notice the result
- **THEN** the Agent SHALL not mention that person
- **AND** fine-grained progress SHALL remain in the Web session timeline rather than the Slack reply

### Requirement: Recovery SHALL continue silently from durable collaboration state
After a process restart, Session recovery, or context compaction, the Agent SHALL reconstruct the active collaboration state from durable records and the Slack thread before continuing. Recovery SHALL preserve the existing Session and reply context. The Agent SHALL NOT announce the interruption, announce the recovery, or ask the human how to proceed solely because recovery occurred. Recovery silence SHALL NOT override the requirement to answer an outstanding direct human question.

#### Scenario: The process restarts while work is in progress
- **WHEN** the Agent resumes after a process restart
- **THEN** it SHALL restore the active state from durable records and the relevant Slack thread
- **AND** it SHALL continue the work in the existing Session and reply target
- **AND** it SHALL send no interruption or recovery announcement

#### Scenario: Context compaction leaves no new information to report
- **WHEN** the Agent resumes after context compaction and has no conclusion, result, failure, or next step to report
- **THEN** it SHALL remain silent
- **AND** it SHALL NOT ask the human to restate the task or choose how to continue

#### Scenario: Recovery leaves an unanswered direct question
- **WHEN** Session recovery or context compaction restores a turn with an outstanding direct human question
- **THEN** the Agent SHALL send the required useful answer through the existing Server-provided reply anchor
- **AND** the reply SHALL NOT contain recovery narration or an interruption preamble

### Requirement: The collaboration contract SHALL be scoped to Slack execution
The `mohist-slack-collaboration` Skill and Slack system facts SHALL be injected only for Slack execution contexts. Web, CLI, and Workflow execution SHALL retain their existing execution envelopes and SHALL NOT receive Slack reply anchors, Slack collaboration instructions, or Slack-specific recovery requirements as a side effect of this change.

#### Scenario: A non-Slack execution is dispatched
- **WHEN** the Server dispatches a Web, CLI, or Workflow execution without Slack provenance
- **THEN** the Runner SHALL build the existing non-Slack execution envelope
- **AND** the envelope SHALL contain no Slack collaboration Skill or Slack system facts

#### Scenario: A Slack follow-up is dispatched alongside normal executions
- **WHEN** the Runner receives a Slack follow-up with a valid Slack execution context
- **THEN** it SHALL inject the Slack Skill and system facts for that follow-up
- **AND** it SHALL leave unrelated non-Slack executions unchanged
