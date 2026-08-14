# Generic Reasoning Effort Capability

### Requirement: Freeze and preserve the canonical execution tuple

The Server MUST preserve runtime, model, reasoning effort, variant, and
capability revision in the durable execution snapshot.

#### Scenario: Effort is independent from variant

- **GIVEN** an Agent selects model `provider/model`, effort `high`, and variant
  `balanced`
- **WHEN** the execution snapshot is created
- **THEN** the snapshot MUST contain those values in separate fields
- **AND** no native runtime thinking-level name may replace either field

### Requirement: Resolve against one complete capability witness

Admission MUST evaluate the frozen tuple against one complete runner catalog
snapshot before claiming work.

#### Scenario: Complete catalog supports the tuple

- **GIVEN** a complete catalog revision lists the model, effort, and variant
- **WHEN** the resolver evaluates the tuple
- **THEN** it MUST return `supported` and the compatible runner identity

#### Scenario: Missing or incomplete catalog remains pending

- **GIVEN** the catalog is absent, incomplete, or has no matching revision
- **WHEN** the resolver evaluates the tuple
- **THEN** it MUST return `needs-setup`
- **AND** it MUST NOT return a terminal configuration failure

#### Scenario: Complete catalog rejects the tuple

- **GIVEN** a complete catalog explicitly lacks the effort or variant
- **WHEN** the resolver evaluates the tuple
- **THEN** it MUST return `incompatible_execution_configuration`
- **AND** it MUST preserve the tuple in the failure evidence

### Requirement: Native translation remains private to the runtime adapter

The Runner MUST translate canonical effort only inside the selected runtime
adapter.

#### Scenario: Pi translates effort without changing variant

- **GIVEN** a supported Pi tuple with effort `high` and variant `balanced`
- **WHEN** the Pi adapter builds its native request
- **THEN** it MAY map `high` to its native thinking level
- **AND** it MUST pass `balanced` as a separate variant value
- **AND** another runtime MUST NOT receive the Pi native value
