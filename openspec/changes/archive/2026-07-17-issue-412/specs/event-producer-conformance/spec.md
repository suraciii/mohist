### Requirement: EventCatalog remains a type catalog

`EventCatalog` SHALL list stable event type names and SHALL NOT maintain a per-type required-lineage
attribute registry. Producer conformance SHALL NOT depend on a `CatalogOnlyTypes` exception list.

#### Scenario: A reserved type has no producer

- **GIVEN** EventCatalog contains a stable reserved type that no domain event serializes today
- **WHEN** conformance tests run
- **THEN** the type remains a catalog entry
- **AND** no fake producer coverage or exclusion is reported as conformance

### Requirement: Conformance rules are defined by producer family

Conformance SHALL determine always-required and conditional attributes from the producer family,
producer context, and emitted event structure. The family rules SHALL match the event-lineage matrix
without repeating a declaration for every event type.

#### Scenario: Issue producer is checked by family rule

- **WHEN** an Issue production path emits any serializable Issue event
- **THEN** conformance requires `projectid` and `issue`
- **AND** requires `epic` exactly when Issue's local affiliation exists

#### Scenario: Workflow producer is checked structurally

- **WHEN** a WorkflowRun production path emits a domain event carrying Stage
- **THEN** conformance requires `projectid`, `workflowrunid`, and `stage`

### Requirement: Every actual production path is exercised

Spec tests SHALL drive WorkflowRun, Issue, Epic, AgentSession, Runner (when produced), and Inbox
envelope construction through their real append/synthesis paths. Every serializable event variant
reachable from each producer SHALL satisfy its family rule.

#### Scenario: A new producer forgets lineage

- **WHEN** a new actual producer or serializable domain event is added without required family context
- **THEN** its production-path conformance spec fails

#### Scenario: Existing producer drops context

- **WHEN** a producer stops stamping a family-required attribute
- **THEN** the corresponding production-path spec fails with the missing attribute

### Requirement: Negative tests prove the guard

The conformance helper SHALL reject absent and empty required context. A focused negative test SHALL
demonstrate failure for each producer-family rule shape rather than only testing successful envelopes.

#### Scenario: Empty required identity is supplied

- **WHEN** a producer-family envelope contains an empty required identity
- **THEN** conformance reports it as missing and the test fails
