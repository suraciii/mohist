### Requirement: Workflow OpenCode turns produce an AgentSession transcript

For every Workflow-source OpenCode turn with an associated Workflow AgentSession, the runner SHALL report the submitted user input and the normalized runtime events produced by that turn to the AgentSession identified by its project, WorkflowRun, and session name. Each report MUST carry the current physical runtime session identity, and successfully accepted reports SHALL be persisted and exposed through the existing Workflow session transcript.

The reported runtime events SHALL preserve the observable facts produced by the shared OpenCode event projection, including assistant text, assistant reasoning, tool-call lifecycle, usage, and resolved model observations. Reports for one turn SHALL preserve their production order, with the submitted input preceding assistant and tool activity.

#### Scenario: Completed plan turn has visible conversation content

- **WHEN** a plan-stage Workflow OpenCode turn submits a user prompt and produces assistant text
- **THEN** the associated Workflow AgentSession SHALL contain the submitted input followed by the assistant text
- **AND** the Workflow session transcript SHALL expose a non-empty turn instead of an empty activity state

#### Scenario: Reasoning and tool activity are recorded in order

- **WHEN** a Workflow OpenCode turn produces reasoning deltas and starts, updates, and completes a tool call
- **THEN** the runner SHALL report the reasoning and tool-call events in their production order against the turn's current runtime session identity
- **AND** the persisted transcript SHALL expose the reasoning and the tool call with its available input, status, and output or error

#### Scenario: Usage and resolved model facts are recorded

- **WHEN** OpenCode reports token usage or a resolved model during a Workflow turn
- **THEN** the runner SHALL report the normalized usage and model events to the associated Workflow AgentSession
- **AND** the session transcript or summary SHALL reflect the accepted usage and model observations

#### Scenario: Reconciled final response supplies missing events

- **WHEN** an assistant message, reasoning part, tool result, usage observation, or model observation is present in the final OpenCode response but was not emitted completely by the live event stream
- **THEN** the runner SHALL report the missing normalized events produced by final-response reconciliation
- **AND** it MUST NOT duplicate content already reported from the live event stream

### Requirement: Transcript reporting is best-effort and independent of the turn result

Workflow AgentSession event uploads SHALL be best-effort. An upload failure MUST be made observable for diagnosis, but MUST NOT prevent the OpenCode prompt from running, change a successful turn to failed, replace the turn's runtime failure, or prevent the Workflow task from receiving the runtime result. Failed uploads SHALL NOT be retried or written to a local fallback by this change.

#### Scenario: Initial input upload fails

- **WHEN** reporting the Workflow turn's input event fails
- **THEN** the runner SHALL still execute the OpenCode turn and return its runtime result
- **AND** the reporting failure SHALL be observable
- **AND** the runner SHALL NOT retry or locally persist the failed upload

#### Scenario: Runtime event upload fails after the turn starts

- **WHEN** reporting an assistant, reasoning, tool, usage, or model event fails
- **THEN** the Workflow turn's success or failure SHALL remain determined by the OpenCode runtime result
- **AND** the reporting failure SHALL be observable without replacing that result

### Requirement: AgentJob transcript reporting remains unchanged

The Workflow reporting behavior SHALL NOT change the AgentJob execution path, its generic AgentSession event route, or the transcript content produced for AgentJob turns.

#### Scenario: AgentJob turn still records its transcript

- **WHEN** an AgentJob OpenCode turn runs after Workflow transcript reporting is enabled
- **THEN** its user input and projected runtime events SHALL continue to be reported through the AgentJob's existing AgentSession path
- **AND** its persisted transcript SHALL retain the existing assistant, reasoning, tool, usage, and model content
