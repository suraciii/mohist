## ADDED Requirements

### Requirement: mo label list surfaces the label catalog

`mo label list` SHALL read the project's label catalog (governed by the `label-catalog` capability) and print each definition's `key`, `description`, and `origin` (and `supportedValues` when present), so agents and users can discover the project's label vocabulary. The command SHALL support the standard output modes (table and JSON). Reading the catalog SHALL NOT alter any Issue's labels.

#### Scenario: List the catalog in table mode
- **WHEN** the user runs `mo label list` for a project with a system `refactor` definition and a user `module` definition
- **THEN** the output lists both definitions
- **AND** each row shows the key, description, and origin

#### Scenario: List the catalog in JSON mode
- **WHEN** the user runs `mo label list --output json`
- **THEN** the output is valid JSON containing each definition's `key`, `description`, `origin`, and `supportedValues` when present

#### Scenario: Catalog with no user definitions still shows system definitions
- **WHEN** the user runs `mo label list` for a project with no user-defined entries
- **THEN** the output still lists the system-seeded definitions such as `refactor`

### Requirement: mo label add and mo label remove manage user catalog entries

The CLI SHALL provide `mo label add` to create a user-origin catalog definition and `mo label remove` to remove one, governed by the `label-catalog` capability. `mo label add` SHALL accept the definition `key`, a `description`, and optional `supportedValues`, and SHALL create an entry with `origin: user`. `mo label remove` SHALL accept the `key` and SHALL remove the matching user-origin entry; it SHALL be idempotent for a missing key. Both commands SHALL reject attempts that target a system-origin definition with a clear error and SHALL NOT alter it.

#### Scenario: Add a user definition
- **WHEN** the user runs `mo label add module --description "Classifies the subsystem" --supported-values auth,ui`
- **THEN** the catalog gains a `module` entry with `origin: user`

#### Scenario: Remove a user definition
- **WHEN** the user runs `mo label remove module`
- **THEN** the `module` entry is removed from the catalog

#### Scenario: Remove a missing definition is idempotent
- **WHEN** the user runs `mo label remove unknown`
- **THEN** the command succeeds with no error

#### Scenario: Remove a system definition is rejected
- **WHEN** the user runs `mo label remove refactor`
- **THEN** the command fails with a clear error stating system definitions are immutable
- **AND** the `refactor` definition remains in the catalog
