### Requirement: EventCatalog declares required lineage attributes per type

`EventCatalog` SHALL rise from a flat list of type names to the protocol registry. For every registered event `type`, `EventCatalog` SHALL declare the set of lineage attributes that type MUST carry on its envelope. The declared required attributes for each family SHALL match the lineage matrix (`workflow.*`, `issue.*`, `epic.*`, `agent-session.*`, `runner.*`, and the inbox-synthesized event).

#### Scenario: Every registered type declares its required lineage attributes

- **WHEN** `EventCatalog` is inspected for any registered event type
- **THEN** it exposes the lineage attribute names that type is required to carry on its envelope

#### Scenario: Stage, task, and check types declare the stage attribute

- **WHEN** a `workflow.stage.*`, `workflow.task.*`, or `workflow.check.*` type is registered
- **THEN** its declared required attributes include `stage` in addition to the base `workflow.*` lineage attributes

### Requirement: Conformance check exercises every event production path

A conformance check SHALL drive every event production path and assert that each emitted envelope satisfies the lineage attributes its catalog entry declares as required. The check SHALL cover all producing stores (workflow run store, issue store, epic grain, agent-session store) and the inbox-synthesized event.

#### Scenario: Every production path emits a conforming envelope

- **WHEN** the conformance check exercises an event production path for a registered type
- **THEN** the emitted envelope's extensions contain every attribute that the type's catalog entry declares as required

### Requirement: Missing required lineage fails the conformance check

The conformance check SHALL fail when an emitted envelope is missing any attribute its catalog entry declares as required. Adding a new event type and forgetting to stamp its lineage SHALL cause the check to fail.

#### Scenario: New event type without stamping fails the check

- **WHEN** a new event type is registered in `EventCatalog` with declared required lineage attributes, but its producer emits an envelope missing one of those attributes
- **THEN** the conformance check fails

#### Scenario: Existing producer dropping a required attribute fails the check

- **WHEN** a producer for a registered type stops stamping a previously required lineage attribute
- **THEN** the conformance check fails for that production path
