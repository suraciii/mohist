### Requirement: Readiness is one of three outcomes computed from the execution definition

The Server SHALL compute an Agent Readiness conclusion from the Agent's execution definition (Instructions, Runtime, Model, Variant, Skills) together with what execution has already confirmed about that definition. The conclusion SHALL be exactly one of `Ready` (the definition is complete and the Server has confirmed no gap), `Needs setup` (the Server has confirmed a configuration gap), or `Unknown` (the Server cannot yet confirm). The conclusion SHALL reflect the Agent's current definition and SHALL be re-evaluated when the definition changes or when a new execution outcome is observed. The Server does not proactively probe Runtime credentials (it has no visibility into them); confirmation of executability comes from observed execution outcomes, not from reading a runtime capability catalog.

#### Scenario: Complete definition confirmed by execution
- **WHEN** the Agent's execution definition is structurally complete and consistent, the Server has confirmed no gap, and a prior execution has succeeded (positive evidence that the referenced Runtime, Model and Variant actually run)
- **THEN** Readiness SHALL be `Ready`

#### Scenario: Confirmed configuration gap
- **WHEN** the Server confirms a gap in the execution definition — a structural or consistency defect (for example a malformed model reference), or a configuration failure revealed by an execution (for example the most recent execution failed with a credential, model, or runtime error the Runner classified)
- **THEN** Readiness SHALL be `Needs setup` and SHALL list the specific gap

#### Scenario: Server cannot yet confirm
- **WHEN** the Agent has never executed or its most recent result is inconclusive, so the Server can neither confirm the definition executes nor confirm a gap
- **THEN** Readiness SHALL be `Unknown`, which is neither `Ready` nor `Needs setup`

### Requirement: Needs setup gives actionable gaps

A `Needs setup` conclusion SHALL enumerate every confirmed configuration gap and point to the single setup entry that resolves it. Each gap SHALL describe what to fix and where, phrased as an actionable next step rather than an internal error.

#### Scenario: Gaps and setup entry returned together
- **WHEN** Readiness is `Needs setup`
- **THEN** the conclusion SHALL carry the list of gaps and a setup entry, and each gap SHALL state the missing configuration and the action to resolve it

### Requirement: Readiness is independent of runner, capacity and concurrency

Runner presence, runner capacity and the Agent's concurrency state SHALL NOT influence Readiness. A `Ready` Agent SHALL NOT become `Needs setup` because no runner is online, capacity is full, or the concurrency limit is reached; those are Availability facts.

#### Scenario: Runner offline does not change a Ready agent
- **WHEN** an Agent is `Ready` and then all runners go offline
- **THEN** Readiness SHALL remain `Ready`

#### Scenario: Capacity or concurrency saturation does not change Readiness
- **WHEN** an Agent is `Ready` and its runner capacity is full or its MaxConcurrentRuns limit is reached
- **THEN** Readiness SHALL remain `Ready`

### Requirement: Readiness is the Server's unified conclusion, visible before launch

The Server SHALL be the sole authority for the Readiness conclusion. `mo agent view` and the equivalent Web Agent view SHALL expose the current Readiness conclusion (and, for `Needs setup`, the gaps and setup entry) before the user submits work. Web and CLI SHALL present the Server's conclusion and SHALL NOT derive a second Readiness verdict from raw Agent config or Runtime capability rules.

#### Scenario: Agent view shows readiness before any launch
- **WHEN** the user views an Agent that has not been launched
- **THEN** the view SHALL show the Server's Readiness conclusion, and for `Needs setup` the specific gaps and setup entry

### Requirement: Needs setup blocks new work; Unknown does not

Submitting new work (a launch) for an Agent whose Readiness is `Needs setup` SHALL be rejected with the gaps and setup entry, and SHALL NOT create an AgentJob or AgentSession. An Agent whose Readiness is `Unknown` SHALL remain submittable; its work waits for validation rather than being blocked.

#### Scenario: Launch rejected on Needs setup
- **WHEN** a launch is submitted for an Agent whose Readiness is `Needs setup`
- **THEN** the launch SHALL be rejected with the gaps and setup entry, and no AgentJob or AgentSession SHALL be created

#### Scenario: Launch accepted on Unknown
- **WHEN** a launch is submitted for an Agent whose Readiness is `Unknown`
- **THEN** the launch SHALL be accepted and the work SHALL wait for validation rather than be rejected
