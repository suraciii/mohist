### Requirement: Timeline distinguishes user input, agent reply, key actions, errors, boundaries, and unknown states

The Session page timeline SHALL interpret an execution by classifying each Server-owned transcript or lifecycle fact into exactly one presentation class: user input with its acceptance and delivery state, agent reply, recognized domain action with a resolved target link, error, Compact or Reset context boundary, or an explicit unknown state. Classification SHALL be a pure derivation from Server facts; the client MUST NOT infer Turn or Session state from heartbeats or item order, and failed recognition MUST degrade to an honest fallback rather than invent semantics. When any item has a failed outcome its class SHALL become error while retaining its original summary and appending the failure fact.

#### Scenario: User input shows its state

- **WHEN** a SessionInput fact is rendered in the timeline
- **THEN** the input item SHALL show the input's acceptance and delivery state
- **AND** inputs that belong to the current AgentTurn SHALL be identifiable as belonging to it

#### Scenario: Recognized Mohist operation becomes a domain action

- **WHEN** a tool or shell fact matches a known Mohist domain operation
- **THEN** the timeline SHALL render it as a domain action in sentence form with a resolved link to its target, such as the Issue page
- **AND** the same operation failing SHALL render as an error that keeps the action's sentence and shows the failure

#### Scenario: Unrecognized operation stays honest

- **WHEN** a command or tool cannot be recognized as a domain operation
- **THEN** the timeline SHALL render it with its normal class and an honest summary such as `Ran X`
- **AND** it MUST NOT be speculatively promoted to a domain action

#### Scenario: Unknown state is explicit and never failed

- **WHEN** an AgentTurn or Session Activity fact is `unknown`
- **THEN** the timeline SHALL present an explicit unknown state distinct from idle and from failure
- **AND** it MUST NOT render unknown as failed or as idle

#### Scenario: Compact and Reset create visible boundaries

- **WHEN** a compaction or context reset occurs
- **THEN** the timeline SHALL render a boundary entry such as `Context reset`
- **AND** later entries SHALL be attributable to the new Runtime context while earlier content remains visible

### Requirement: Low-value noise collapses without hiding failures or domain actions

The timeline SHALL collapse a consecutive run of at least three low-salience items of one class into a single expandable summary such as `Read 5 files`. Error, domain-action, input, agent-reply, status, boundary, and suppressed items MUST never enter a collapsed group, and any such item SHALL break a consecutive run. Collapsed groups SHALL remain expandable to their individual items.

#### Scenario: Consecutive reads collapse

- **WHEN** five consecutive successful read or search items occur
- **THEN** the timeline SHALL render one collapsed summary entry
- **AND** expanding it SHALL reveal each individual item

#### Scenario: A failure breaks the run and stays visible

- **WHEN** a run of collapsible items contains a failed operation
- **THEN** the items before the failure SHALL collapse, the failure SHALL render as a prominent error outside any group
- **AND** the remaining items SHALL form a new group rather than hiding the failure

#### Scenario: Domain action never collapses

- **WHEN** a recognized domain action occurs inside a run of low-salience items
- **THEN** the domain action SHALL render outside the collapsed group with its outcome visible

### Requirement: Raw events remain an explicit diagnostic view

The Session page SHALL provide an explicit raw view toggle that presents the same underlying timeline data in raw fact order — one row per fact with an expandable payload — as a diagnostic for why an execution produced its result. The interpreted and raw views SHALL be two presentations of the same data, not two feeds, and switching between them SHALL preserve the scroll anchor by item identity.

#### Scenario: Toggling to the raw view

- **WHEN** the user switches the timeline to the raw view
- **THEN** the page SHALL render one row per transcript fact in fact order with an expandable payload
- **AND** the visible item SHALL remain in view by anchoring the scroll to the corresponding item identity

#### Scenario: Raw view explains a result

- **WHEN** the interpreted timeline shows a failure or unexpected outcome
- **THEN** the user SHALL be able to diagnose it from the raw view without a separate data source
- **AND** the raw view MUST NOT reorder, filter, or add facts relative to the interpreted view's sources

### Requirement: Timeline vocabulary matches the history contract

The Session page timeline SHALL present results, context references, Job and Turn identity, and failure interpretation using the same vocabulary and facts as the Agent execution history records: the same context reference envelope, the same Job/Turn/result terms, and the same failure reason and category interpretation. The timeline MUST NOT re-arbitrate Job, Turn, or Session state and MUST NOT introduce result facts that the history projection cannot show for the same execution.

#### Scenario: Same result reads the same

- **WHEN** a Turn's result appears both as a history record and in the Session page timeline
- **THEN** both surfaces SHALL present the same outcome status, result summary, and failure reason
- **AND** a discrepancy between the two presentations SHALL be impossible for the same Turn identity

#### Scenario: Same context envelope

- **WHEN** a Session carries launch context references
- **THEN** the Session page SHALL present the same context references, with the same absence semantics, as the history record for that execution

### Requirement: Refresh keeps the same understanding

The Session page SHALL anchor its timeline by stable Turn identity. Opening the page through a link that targets a Turn SHALL position the timeline on that Turn, and a refresh of the anchored page SHALL retain the anchor and present the same interpretation of the execution, including results, boundaries, and context.

#### Scenario: Opening a history link anchors the Turn

- **WHEN** the user opens a Session page from a history record link that identifies a Turn
- **THEN** the timeline SHALL present that Turn at the anchored position
- **AND** the surrounding interpretation SHALL present that Turn's inputs, result, and context

#### Scenario: Refresh preserves the anchor

- **WHEN** the anchored Session page is refreshed
- **THEN** the page SHALL restore the same Turn anchor and the same timeline interpretation
- **AND** the user SHALL not be returned to an unanchored or bottom-of-transcript position
