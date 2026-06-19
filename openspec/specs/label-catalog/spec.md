# OpenSpec Capability: label-catalog

### Requirement: Label catalog stores project-scoped label definitions

The label catalog SHALL store, per project, a set of `LabelDefinition` entries. Each entry SHALL have a `key`, a non-empty `description`, an `origin` of either `system` or `user`, and an optional `supportedValues` list. The `key` SHALL be unique within a project's catalog. Definitions belonging to one project's catalog SHALL NOT be visible in any other project's catalog.

#### Scenario: Definition exposes required fields
- **WHEN** a label definition is stored for key `refactor`
- **THEN** the entry exposes `key` equal to `refactor`, a non-empty `description`, and `origin` of either `system` or `user`
- **AND** an absent `supportedValues` is represented as no list (the field is optional)

#### Scenario: Duplicate key within a project is rejected
- **WHEN** a user-defined definition is added with key `refactor` while a definition with key `refactor` already exists in the same project's catalog
- **THEN** the add is rejected with a clear error
- **AND** the existing definition is unchanged

#### Scenario: Catalog is scoped per project
- **WHEN** project A has a user-defined definition for key `module` and project B's catalog is read
- **THEN** project B's catalog does not contain the `module` definition from project A

### Requirement: Label definition keys and values are validated

A `LabelDefinition` key SHALL match `^[a-z0-9]([-a-z0-9]*[a-z0-9])?$` (lowercase ASCII alphanumeric characters and interior dashes), consistent with the `issue-labels` capability. A `description` SHALL be a non-empty, non-whitespace string. Each entry in `supportedValues`, when the list is present, SHALL be a non-empty, non-whitespace string. An invalid key, an empty description, or an empty supported value SHALL be rejected with a clear error and SHALL NOT be persisted.

#### Scenario: Valid definition is accepted
- **WHEN** a definition is created with key `module`, description "Classifies the subsystem an issue touches", and supportedValues `["auth", "ui", "persistence"]`
- **THEN** the definition is accepted and persisted

#### Scenario: Invalid key is rejected
- **WHEN** a definition is created with key `Module` (uppercase) or `-mod` (leading dash)
- **THEN** the definition is rejected with a clear error and is not persisted

#### Scenario: Empty description is rejected
- **WHEN** a definition is created with a whitespace-only description
- **THEN** the definition is rejected with a clear error and is not persisted

#### Scenario: Empty supported value is rejected
- **WHEN** a definition is created with supportedValues containing an empty string
- **THEN** the definition is rejected with a clear error and is not persisted

### Requirement: System-defined catalog entries are seeded and immutable

The catalog SHALL seed a set of system-origin definitions (`origin: system`) available to every project. The seed set SHALL include at least `refactor`, whose description captures the "Refactor label discipline" guidance: technical refactoring that changes internal code or architecture to reduce complexity and lower the cost of future change without altering observable behavior. System-origin definitions SHALL be read-only: they SHALL NOT be modified or removed by user actions. Only user-origin definitions (`origin: user`) SHALL be creatable, modifiable, and removable by users.

#### Scenario: refactor is present as a system definition
- **WHEN** a project's catalog is read
- **THEN** it contains a definition with key `refactor` and `origin: system`
- **AND** the description explains that `refactor` is for changes that do not alter observable behavior

#### Scenario: System definition cannot be removed
- **WHEN** a user attempts to remove the system-origin `refactor` definition
- **THEN** the removal is rejected with a clear error
- **AND** the `refactor` definition remains in the catalog

#### Scenario: System definition cannot be modified
- **WHEN** a user attempts to change the description or supportedValues of the system-origin `refactor` definition
- **THEN** the modification is rejected with a clear error
- **AND** the `refactor` definition is unchanged

#### Scenario: User definition is created as user origin
- **WHEN** a user adds a definition with key `module`
- **THEN** the resulting entry has `origin: user`
- **AND** it can later be modified or removed by the user

### Requirement: Label catalog is advisory and does not constrain issue labels

The catalog SHALL describe and recommend labels but SHALL NOT validate, reject, or otherwise constrain Issue labels based on catalog membership. An Issue SHALL be permitted to carry a label whose key is absent from the project's catalog, and a catalog entry SHALL be permitted to have no corresponding Issue label. Reading or changing the catalog SHALL NOT alter any Issue's labels, and reading or changing an Issue's labels SHALL NOT alter the catalog. The catalog SHALL NOT introduce any server-side AI classification and SHALL NOT invoke any agent.

#### Scenario: Issue may carry a label absent from the catalog
- **WHEN** an Issue is labeled with `priority` and the project's catalog has no `priority` definition
- **THEN** the label is accepted on the Issue
- **AND** no error is raised and no catalog entry is auto-created

#### Scenario: Catalog entry may be unused
- **WHEN** a project's catalog defines `module` but no Issue carries a `module` label
- **THEN** the catalog entry is retained unchanged

#### Scenario: Catalog changes do not touch issue labels
- **WHEN** a user adds, updates, or removes a catalog definition
- **THEN** no Issue's labels are modified as a side effect

#### Scenario: Catalog is pure data with no AI or agent invocation
- **WHEN** the catalog is read or written
- **THEN** no server-side AI model is invoked and no agent is called
