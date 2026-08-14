### Requirement: Four executability states derived by the Server

Agent executability SHALL have exactly four states: `not-configured`, `not-executable`, `unknown`, and `executable`. The Server SHALL derive the state from the definition's structural completeness and the Agent's execution history, and the derived state SHALL be authoritative for every surface:

- `not-configured` means setup is incomplete — the definition has structural gaps such as missing instructions, a missing or malformed model reference, or an unsupported runtime.
- `not-executable` means the definition is structurally complete but blocked — the latest execution evidence matching the current definition records an execution-configuration failure, for example provider authentication rejected, the configured model unusable, or the runtime rejecting the definition.
- `unknown` means there is no execution evidence that matches the current definition; surfaces MUST NOT infer success or failure from it.
- `executable` means the definition is structurally complete and the latest matching execution evidence confirms it executes.

#### Scenario: A structural gap yields not-configured

- **WHEN** an Agent definition is missing required definition content, such as instructions or a model
- **THEN** the Server SHALL derive executability `not-configured`
- **AND** the projection SHALL carry one gap per structural defect, each with its next action

#### Scenario: An execution-configuration failure yields not-executable

- **WHEN** an Agent's latest execution that matches the current definition failed with a configuration failure such as provider authentication or an unusable model
- **THEN** the Server SHALL derive executability `not-executable`
- **AND** the projection SHALL distinguish the configuration failure from a structural definition gap

#### Scenario: No matching evidence yields unknown

- **WHEN** an Agent's definition is structurally complete but has no execution evidence matching the current definition
- **THEN** the Server SHALL derive executability `unknown`
- **AND** no surface SHALL present the Agent as launch-verified or as failed

#### Scenario: Confirmed execution yields executable

- **WHEN** an Agent's definition is structurally complete and its latest execution evidence matching the current definition completed successfully
- **THEN** the Server SHALL derive executability `executable`

### Requirement: Executability and Availability remain separate signals

Executability — a definition-and-history diagnosis — and Availability — a transient Runner, capacity, and concurrency condition — MUST remain separate signals. No surface SHALL merge them into one badge, derive one from the other, or let an Availability condition change the executability state or the reverse. Each signal SHALL carry its own label and its own actionable content: executability carries gaps and next actions; Availability carries waiting reasons.

#### Scenario: An executable Agent with no online Runner

- **WHEN** an Agent's executability is `executable` and no Runner is online
- **THEN** the Agent's executability SHALL remain `executable`
- **AND** Availability SHALL separately report why work cannot start now

#### Scenario: A not-configured Agent with free capacity

- **WHEN** an Agent's executability is `not-configured` and Runners are online with free capacity
- **THEN** the Agent's executability SHALL remain `not-configured` with its gaps
- **AND** the free capacity MUST NOT be presented as making the Agent launchable

#### Scenario: One badge is never synthesized

- **WHEN** any surface renders an Agent in list or detail
- **THEN** executability and Availability SHALL each be rendered as their own labeled signal
- **AND** the surface MUST NOT combine them into a single verdict or badge

### Requirement: Actionable gaps and next actions in list and detail

For `not-configured` and `not-executable`, the executability projection SHALL include every specific gap and, for each gap, a next action and the entry point where the fix is made. For `unknown`, the projection SHALL state that execution evidence is pending and what happens when the Agent is launched. The Agent list and the Agent detail, in both the Web UI and the CLI, SHALL render the state together with this actionable content.

#### Scenario: Detail shows the full diagnosis

- **WHEN** a user opens the detail of an Agent whose executability is `not-executable`
- **THEN** the detail SHALL show the state, the configuration-failure gap, its next action, and the fix entry point

#### Scenario: The list shows the state without losing the diagnosis

- **WHEN** a user views the Agent list containing a `not-configured` Agent
- **THEN** the list SHALL show the executability state and lead the user to the gap's next action
- **AND** following that action SHALL lead to the surface where the gap is fixed

#### Scenario: Unknown states what launch will do

- **WHEN** a user views an Agent whose executability is `unknown`
- **THEN** the surface SHALL state that a launch will be accepted and will wait for Runner verification
- **AND** it MUST NOT present the state as an error or a failure

### Requirement: Launch gating follows the state

The dispatch path SHALL gate new launches on executability. An Agent whose executability is `not-configured` or `not-executable` MUST NOT accept new work, and the rejection SHALL carry the derived gaps and next actions. An Agent whose executability is `unknown` or `executable` SHALL accept new work. No entry point may bypass the gate by deriving its own executability verdict.

#### Scenario: Launching a not-configured Agent

- **WHEN** a caller launches an Agent whose executability is `not-configured`
- **THEN** the launch SHALL be rejected before any AgentJob or AgentSession is created
- **AND** the rejection SHALL carry each gap's message, action, and fix entry point

#### Scenario: Launching a not-executable Agent

- **WHEN** a caller launches an Agent whose executability is `not-executable`
- **THEN** the launch SHALL be rejected before any AgentJob or AgentSession is created
- **AND** the rejection SHALL distinguish the execution-configuration failure from missing setup

#### Scenario: Launching an unknown Agent

- **WHEN** a caller launches an Agent whose executability is `unknown`
- **THEN** the launch SHALL be accepted and the resulting work SHALL wait for Runner verification

### Requirement: One Server projection drives Web and CLI

The four-state executability, its gaps, and its next actions SHALL come from one Server-authoritative projection. The Web Agent list and detail and the CLI agent list and view SHALL render that projection; a client MUST NOT synthesize, override, or second-guess the Server's state. Editing the definition SHALL re-derive the state on the next read.

#### Scenario: Consistent rendering across surfaces

- **WHEN** an Agent whose executability is `not-executable` is viewed in the Web detail and via `mo agent view`
- **THEN** both surfaces SHALL show the same state, gaps, and next actions from the Server projection

#### Scenario: Fixing the definition re-derives the state

- **WHEN** the defect that caused `not-configured` is fixed by a definition edit
- **THEN** the next read SHALL re-derive executability from the updated definition and its execution evidence, yielding `unknown` when no matching evidence exists
- **AND** the stale `not-configured` state MUST NOT persist after the definition is complete
