# OpenSpec Capability: model-reasoning-variants

### Requirement: Reasoning variant is an optional model-bound companion

A reasoning variant (推理档位) SHALL be an optional value bound to a model that lets a user dial a model's reasoning effort without changing the model. A variant SHALL be configurable on every surface where a model is configurable: Agent definitions, the issue default model and per-stage model overrides, and project-level and per-stage model defaults. When no variant is set for a model, system behavior SHALL be identical to behavior before this capability existed.

#### Scenario: Variant configurable beside each model surface

- **WHEN** a user configures a model on an Agent definition, the issue default selector, a per-stage issue override, a project default, or a per-stage project default
- **THEN** that surface SHALL also accept an optional reasoning variant bound to the selected model
- **AND** the variant SHALL be stored together with the model it accompanies

#### Scenario: Absent variant preserves prior behavior

- **WHEN** a model is configured with no variant
- **THEN** the system SHALL behave identically to behavior before this capability existed
- **AND** no variant SHALL be injected into coder sessions, persisted metadata, or dispatch

### Requirement: Variant validity is model-dependent

A stored variant SHALL be valid only for the model it was chosen with. When the model it was selected for is changed or cleared, the previously stored variant SHALL NOT be assumed valid, and the system SHALL NOT guarantee it applies. The system SHALL NOT hard-reject a stored variant whose current model no longer reports support for it; instead the legal set SHALL be re-derived for the new model, and a variant the new model does not support SHALL be dropped from selection while remaining subject to best-effort delivery.

#### Scenario: Model change re-derives the legal variant set

- **WHEN** a user changes the selected model from one model to another
- **THEN** the legal variant set SHALL be re-derived from the new model's reported variants
- **AND** the variant selector SHALL no longer present variants the new model does not support

#### Scenario: Clearing the model clears the bound variant

- **WHEN** a configured model is cleared (set to null or empty)
- **THEN** any variant bound to that model SHALL also be cleared
- **AND** no orphaned variant SHALL remain without a model

#### Scenario: Stored unsupported variant is not hard-rejected

- **WHEN** an issue or Agent stores a variant whose current model no longer reports support for it
- **THEN** the system SHALL NOT reject the stored record as invalid
- **AND** the stored variant SHALL be dropped from selection and treated as best-effort at delivery

### Requirement: Variant legal set is discovery-sourced and non-enumerable

The set of legal variants for a model SHALL be the set returned alongside that model during model discovery. The legal set SHALL differ per model, including the empty set for models that support no variants. Legal variants SHALL NOT be modeled as a fixed enumeration. Selection surfaces SHALL derive the presented variant set directly from discovery results and SHALL NOT require runtime probing or session creation to determine the legal set.

#### Scenario: Discovery provides variants per model

- **WHEN** model discovery reports a model together with its supported variant set
- **THEN** the legal variant set for that model SHALL be exactly the reported set
- **AND** variants not in the reported set SHALL NOT be presented as legal

#### Scenario: Model with no variants yields the empty set

- **WHEN** model discovery reports a model that supports no variants
- **THEN** that model's legal variant set SHALL be empty
- **AND** no variant selector SHALL be presented for that model

#### Scenario: Selector derives from discovery without runtime probing

- **WHEN** a variant selector determines which variants to present for a model
- **THEN** it SHALL use the variant set already returned by model discovery
- **AND** it SHALL NOT create a coder session or otherwise probe the model at runtime to discover variants

### Requirement: Variant delivery to coder sessions is best-effort

Delivering a variant to a coder session that the session's model does not support SHALL NOT turn an otherwise-successful run into a failed run. A best-effort variant that the model ignores or rejects SHALL be treated as non-fatal session input. The absence of a variant SHALL NOT be treated as an error.

#### Scenario: Unsupported delivered variant does not fail the run

- **WHEN** a coder session is dispatched with a variant the model does not support
- **AND** the run would otherwise succeed
- **THEN** the run SHALL complete successfully
- **AND** the unsupported variant SHALL NOT be recorded as the run's failure reason

#### Scenario: Absent variant is not an error

- **WHEN** a coder session is dispatched with no variant
- **THEN** the session SHALL launch and run normally
- **AND** the absence of a variant SHALL NOT be reported as an error condition

#### Scenario: Supported variant takes effect

- **WHEN** a coder session is dispatched with a variant the model reports as supported
- **THEN** the variant SHALL be applied to the session before prompt execution
- **AND** the session SHALL run with the selected reasoning effort
