### Requirement: The detail page shows the execution definition summary

The Agent detail page SHALL present the Agent's execution definition in a readable summary: identity (name and description), Instructions, Runtime, Model, Variant, Skills, Max concurrent runs, and the active/archived state. The page SHALL provide an edit entry that opens the execution-definition editor for these fields.

#### Scenario: The definition summary is readable

- **WHEN** a user opens an active Agent's detail page
- **THEN** the page SHALL display the Agent's name, description, Instructions, Runtime/Model/Variant, Skills, Max concurrent runs, and active state

#### Scenario: An edit entry opens the definition editor

- **WHEN** a user activates the edit entry on the detail page
- **THEN** the execution-definition editor SHALL open, populated with the Agent's current definition fields

### Requirement: Editing communicates that it only affects future Jobs

The detail page SHALL make clear that edits to Instructions, Runtime, Model, Variant and Skills take effect only on AgentJobs created after the edit; already-running executions and existing AgentSessions SHALL continue using the configuration fixed at their launch. The page SHALL NOT imply that an edit reconfigures an in-flight execution.

#### Scenario: The edit timing is stated before a save

- **WHEN** a user opens the execution-definition editor from the detail page
- **THEN** the page or editor SHALL state that the change applies to future Jobs, not to executions already in progress

### Requirement: Readiness is explained with specifics and a next step

The detail page SHALL display the server's Readiness conclusion (Ready, Needs setup, or Unknown). When the conclusion is Needs setup, the page SHALL list each gap the server reports (its message and the action to fix it) and SHALL surface the single next step — a link to the place where the gap is fixed, using the server-provided setup label and path. When the conclusion is Unknown, the page SHALL explain that the server has not confirmed the Agent and that new work will wait for validation. The page SHALL NOT synthesize gaps or a setup entry that the server did not provide.

#### Scenario: Needs setup lists gaps and the fix entry

- **WHEN** the server reports Readiness as Needs setup with one or more gaps and a setup entry
- **THEN** the detail page SHALL display each gap's message and action, and SHALL render a link using the server-provided setup label pointing at the server-provided setup path

#### Scenario: Unknown explains wait-for-validation without inventing gaps

- **WHEN** the server reports Readiness as Unknown
- **THEN** the detail page SHALL explain that the Agent is unconfirmed and that new work will wait for validation, and SHALL NOT present any Needs-setup gap

### Requirement: Availability is rendered separately from Readiness

The detail page SHALL present Availability (whether a new execution can start now, or is waiting) as a signal distinct from Readiness. Runner offline, runner capacity full, agent concurrency limit reached, and dispatch-pending SHALL be shown as Availability, and SHALL NOT be shown as a Readiness conclusion or a configuration gap. The page SHALL display active runs (against Max concurrent runs when set) and runner slot usage as Availability facts. The page SHALL NOT derive an Availability or capacity verdict from raw runner slots independent of the server's conclusion.

#### Scenario: Capacity-full reads as Availability, not configuration

- **WHEN** the server reports Availability as waiting with reason capacity-full (or concurrency-limit, or no-online-runner) while Readiness is Ready
- **THEN** the detail page SHALL show the waiting state and reason under Availability, and SHALL keep Readiness as Ready

#### Scenario: The page does not invent a capacity verdict from raw slots

- **WHEN** the server reports Availability as can-start-now even though raw runner slots appear full
- **THEN** the detail page SHALL show Availability as ready and SHALL NOT display a derived "at capacity" verdict contradicting the server
