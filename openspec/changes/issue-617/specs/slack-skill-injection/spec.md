### Requirement: Slack origin requires a complete versioned execution context
Every Slack-origin initial launch and Slack-origin follow-up SHALL carry a Server-created execution context containing the current context version, the versioned `mohist-slack-collaboration` Skill name and instructions with its content digest, and a complete reply anchor. The reply anchor SHALL identify the workspace, conversation, thread root, triggering message, initiating member, Connection, Session, and dispatch operation. A Slack-origin execution with a missing or incomplete context SHALL be treated as invalid rather than as a non-Slack execution.

#### Scenario: A direct-message initial launch carries its Slack context
- **WHEN** a valid Slack direct message starts a new Agent Session
- **THEN** the initial AgentJob dispatch SHALL carry a complete Slack execution context
- **AND** the context SHALL identify the direct-message conversation and the triggering message as the thread root
- **AND** it SHALL identify the Slack Connection, initiating member, Session, and initial dispatch reference
- **AND** it SHALL carry the versioned collaboration Skill and its matching content digest

#### Scenario: A channel-root initial launch carries its Slack context
- **WHEN** a valid Slack channel-root mention starts a new Agent Session
- **THEN** the initial AgentJob dispatch SHALL carry a complete Slack execution context
- **AND** the context SHALL identify the root message as both the thread root and triggering message
- **AND** it SHALL preserve the Server-resolved workspace, conversation, Connection, member, Session, and dispatch identities

#### Scenario: A Slack follow-up carries a new Server-provided reply anchor
- **WHEN** a valid Slack follow-up is dispatched for an existing Session
- **THEN** the follow-up request SHALL carry the same published collaboration Skill name, version, instructions, and digest as a Slack initial launch
- **AND** it SHALL carry a complete reply anchor for the follow-up
- **AND** the anchor SHALL preserve the bound thread root while identifying the follow-up message as the triggering message
- **AND** the anchor SHALL identify the current Session and follow-up dispatch operation

### Requirement: Initial launches and follow-ups receive the same validated Skill and anchor
The Server SHALL deliver the validated Slack Skill and Server-provided reply anchor to both Slack-origin initial execution and Slack-origin follow-up execution. The Runner SHALL preserve the Skill instructions as managed execution-definition input and SHALL expose the anchor as Slack system facts for the same execution. The Runner SHALL NOT replace the Server-provided destination with a Runtime-selected or Agent-invented destination.

#### Scenario: Initial and follow-up execution compose equivalent Slack controls
- **WHEN** one Slack-origin Session performs an initial launch and later performs a follow-up
- **THEN** both Runtime invocations SHALL receive the same collaboration Skill identity, version, instruction body, and digest after validation
- **AND** each invocation SHALL receive the Server-provided anchor for its own input
- **AND** the initial and follow-up prompts SHALL retain their own input text while the Slack control data remains system-provided

### Requirement: Runner validates Slack context integrity before Runtime invocation
The Runner SHALL validate a Slack execution context before invoking an Agent Runtime. Validation SHALL reject an unsupported context version, a non-object or malformed context, any missing or empty required reply-anchor field, any missing or empty Skill name, version, instructions, or content digest, and any content digest that does not match the exact supplied instructions. A rejected context SHALL fail closed before the follow-up input is enqueued or the Runtime is invoked.

#### Scenario: A modified Skill body is rejected
- **WHEN** a Slack execution context contains a collaboration Skill whose instructions have been changed without updating the digest
- **THEN** the Runner SHALL reject the execution context
- **AND** it SHALL not invoke the Runtime
- **AND** it SHALL not enqueue the Slack follow-up input for execution

#### Scenario: An anchorless or incomplete context is rejected
- **WHEN** a Slack-origin execution context omits the reply anchor or omits any required anchor identifier
- **THEN** the Runner SHALL reject the context as invalid
- **AND** it SHALL not invoke the Runtime
- **AND** it SHALL not allow execution to proceed as if it were non-Slack

#### Scenario: An unsupported context version is rejected
- **WHEN** a Slack execution context carries a version the Runner does not support
- **THEN** the Runner SHALL reject the context before Runtime invocation
- **AND** it SHALL not use the context's Skill instructions or reply anchor

### Requirement: Non-Slack execution remains free of Slack injection
An execution that does not originate from Slack SHALL continue without a Slack execution context, the Slack collaboration Skill, or Slack reply-anchor system facts. Existing non-Slack Agent instructions and configured Skills SHALL continue to be composed and delivered according to their existing contract.

#### Scenario: A normal Agent launch has no Slack control data
- **WHEN** an Agent is launched from the Web UI, CLI, Workflow, or another non-Slack source
- **THEN** the execution envelope SHALL contain no Slack collaboration Skill
- **AND** it SHALL contain no Slack reply anchor or Slack system-facts block
- **AND** the Agent SHALL retain its existing prompt, instructions, and configured Skills

#### Scenario: A non-Slack follow-up is not upgraded to Slack execution
- **WHEN** a non-Slack Session receives a follow-up
- **THEN** the follow-up SHALL be delivered without Slack Skill instructions and without a Slack reply anchor
- **AND** Slack context validation SHALL not be required for that follow-up

### Requirement: Agent reply authorship remains separate from context injection
This change SHALL keep reply authorship with the Agent's existing Slack reply action. Server and Runner SHALL NOT classify natural-language questions to manufacture replies, detect missing replies to generate fallback content, or convert Runtime output into Slack content solely because a Slack execution context was injected. Slack delivery SHALL remain separate from Runtime output and execution state.

#### Scenario: A validated context does not create a Server-authored answer
- **WHEN** a Slack-origin Runtime invocation completes with a validated Skill and anchor but the Agent sends no reply
- **THEN** the Server SHALL not synthesize a missing-answer message as part of this change
- **AND** the Runtime output SHALL not be copied into Slack automatically
- **AND** any Slack reply SHALL still be authored through the Agent's existing send action
