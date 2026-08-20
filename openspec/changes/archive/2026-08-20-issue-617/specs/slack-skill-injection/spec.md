### Requirement: Slack origin requires a complete versioned execution context
Every Slack-origin initial launch and Slack-origin follow-up SHALL carry a Server-created execution context containing the current context version, the versioned `mohist-slack-collaboration` Skill name and instructions with its content digest, and a complete reply anchor. Every initial and follow-up dispatch SHALL also carry an explicit `executionSource` discriminator (`slack` or `non-slack`); `slack` SHALL require the context and `non-slack` SHALL require its absence. The reply anchor SHALL identify the workspace, conversation, durable bound thread root, triggering message, initiating member, Connection, Session, and dispatch operation. A Slack-origin execution with a missing or incomplete context SHALL be treated as invalid rather than as a non-Slack execution.

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
- **WHEN** a valid single-input Slack follow-up is dispatched for an existing Session
- **THEN** the follow-up request SHALL declare `executionSource: slack`
- **AND** it SHALL carry the same published collaboration Skill name, version, instructions, and digest as a Slack initial launch
- **AND** it SHALL carry a complete reply anchor for the follow-up
- **AND** the anchor SHALL preserve the durable bound thread root while identifying the follow-up message as the triggering message
- **AND** the anchor SHALL identify the current Session and follow-up dispatch operation

#### Scenario: A DM follow-up preserves its initial bound root
- **WHEN** a follow-up is dispatched for a Slack DM whose incoming provenance has no thread timestamp
- **THEN** the anchor SHALL use the initial DM message's durable id as `threadRootMessageId`
- **AND** it SHALL use the follow-up message id as `triggeringMessageId`
- **AND** it SHALL reject the dispatch if that persisted bound root is unavailable rather than substituting the follow-up message as the root

#### Scenario: A batched Slack follow-up has a deterministic representative anchor
- **WHEN** multiple queued Slack inputs are assigned to one existing follow-up turn
- **THEN** the combined dispatch SHALL retain all input texts
- **AND** the anchor SHALL preserve the Session's durable bound thread root
- **AND** `triggeringMessageId` SHALL be the message id from the first `InputId` in the persisted `turn.InputIds` order
- **AND** `dispatchRef` SHALL identify the combined follow-up operation
- **AND** the Server SHALL reject the dispatch when that representative input or its Slack provenance is unavailable rather than guessing a destination

### Requirement: Initial launches and follow-ups receive the same validated Skill and anchor
The Server SHALL deliver the validated Slack Skill and Server-provided reply anchor to both Slack-origin initial execution and Slack-origin follow-up execution. The Runner SHALL preserve the Skill instructions as managed execution-definition input and SHALL expose the anchor as Slack system facts for the same execution. The Runner SHALL NOT replace the Server-provided destination with a Runtime-selected or Agent-invented destination.

#### Scenario: Initial and follow-up execution compose equivalent Slack controls
- **WHEN** one Slack-origin Session performs an initial launch and later performs a follow-up
- **THEN** both Runtime invocations SHALL receive the same collaboration Skill identity, version, instruction body, and digest after validation
- **AND** that shared digest SHALL be `dedf18a796543ade06a9e0ece00c086577153e1e633f868c099b01cf910d641b`
- **AND** each invocation SHALL receive the Server-provided anchor for its own input
- **AND** the initial and follow-up prompts SHALL retain their own input text while the Slack control data remains system-provided

### Requirement: Source discriminator rollout preserves existing dispatches
The Server and Runner SHALL introduce the required `executionSource` discriminator through a bounded compatibility phase. While strict source validation is disabled, an upgraded Runner MAY accept a pre-existing source-less payload only through an explicitly legacy path that preserves its prior behavior; it SHALL NOT relabel the payload as `non-slack`. Explicit `slack` and `non-slack` values SHALL be validated immediately. Before strict validation is enabled, all Server producers SHALL emit explicit source/context pairs, version-1 dispatches SHALL be routed only to Runners that understand them, and pre-existing source-less work SHALL be drained or reconciled from trusted durable provenance. Once strict validation is enabled, an omitted or unknown source SHALL be rejected and SHALL NOT fall through to ordinary work.

#### Scenario: Legacy source-less work remains compatible only during the bounded rollout phase
- **WHEN** an upgraded Runner receives a pre-existing source-less dispatch while compatibility mode is enabled
- **THEN** it MAY process the dispatch through the legacy path
- **AND** it SHALL NOT reinterpret the omitted source as `non-slack`
- **AND** it SHALL record a diagnostic so the source-less work can be drained before strict validation
- **AND** an explicit `slack` source without context SHALL still be rejected

#### Scenario: New Server dispatches do not target an old Runner
- **WHEN** a Server emits an explicit version-1 source/context pair and the selected Runner does not advertise version-1 support
- **THEN** capability routing SHALL hold or reject the dispatch before delivery
- **AND** it SHALL not send the pair to a Runner that would ignore the source or context
- **AND** the dispatch SHALL be retried or surfaced as unavailable without being converted to ordinary work

#### Scenario: Strict validation starts after legacy work drains
- **WHEN** all producers emit explicit sources, no pending source-less dispatch remains, and strict validation is enabled
- **THEN** an omitted or unknown `executionSource` SHALL be rejected
- **AND** a missing source SHALL not be treated as `non-slack`
- **AND** valid explicit non-Slack work SHALL retain its existing envelope

### Requirement: Runner validates Slack context integrity before Runtime invocation
The Runner SHALL validate the `executionSource` and Slack execution context before invoking an Agent Runtime. A `slack` source SHALL require a present context; a `non-slack` source SHALL require no context. Validation SHALL reject an omitted or unknown source, a Slack source with an omitted or null context, a non-Slack source carrying context, an unsupported context version, a non-object or malformed context, any missing or empty required reply-anchor field, any missing or empty Skill name, version, instructions, or content digest, and any content digest that does not match the exact supplied instructions. A rejected context SHALL fail closed before the follow-up input is enqueued or the Runtime is invoked.

#### Scenario: A modified Skill body is rejected
- **WHEN** a Slack execution context contains a collaboration Skill whose instructions have been changed without updating the digest
- **THEN** the Runner SHALL reject the execution context
- **AND** it SHALL not invoke the Runtime
- **AND** it SHALL not enqueue the Slack follow-up input for execution

#### Scenario: An initial Slack source without context is rejected
- **WHEN** an initial AgentJob dispatch declares `executionSource: slack` but its context is omitted or null
- **THEN** the Runner SHALL reject the dispatch before Runtime selection or invocation
- **AND** it SHALL not treat the AgentJob as ordinary non-Slack work

#### Scenario: A follow-up Slack source without context is rejected
- **WHEN** a follow-up dispatch declares `executionSource: slack` but its context is omitted or null
- **THEN** the Runner control dispatcher and follow-up handler SHALL reject it before local input enqueue
- **AND** it SHALL not invoke the Runtime
- **AND** it SHALL not treat the follow-up as ordinary non-Slack work

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
- **THEN** the dispatch SHALL declare `executionSource: non-slack` and carry no Slack context
- **AND** the execution envelope SHALL contain no Slack collaboration Skill
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
