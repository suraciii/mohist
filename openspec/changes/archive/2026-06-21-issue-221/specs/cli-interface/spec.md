## ADDED Requirements

### Requirement: mo label update modifies user catalog entries

The CLI SHALL provide `mo label update <key>` to modify an existing user-origin label catalog definition, governed by the `label-catalog` capability and peer to `mo label list`/`add`/`remove`. The command SHALL send `PATCH /api/projects/{projectRef}/labels/catalog/{key}` carrying only the fields the user supplies via `--description` and/or `--supported-values`; fields the user omits SHALL retain their current value (partial update). The command SHALL accept the standard `--project`/`--project-id` overrides shared by the `mo label` group (matching `add`/`remove`, which print the API response directly and do not expose an output-mode flag). The command SHALL validate the `key` against `^[a-z0-9]([-a-z0-9]*[a-z0-9])?$` before calling the API. When `--description` is supplied it SHALL be a non-empty, non-whitespace string, and each comma-separated entry of `--supported-values` SHALL be non-empty. The command SHALL surface every server error clearly — an unknown key, a validation failure, a system-definition modification attempt, or any conflict — and SHALL exit with a non-zero status without reporting success.

#### Scenario: Update description only
- **WHEN** the user runs `mo label update module --description "Classifies the subsystem an issue touches"`
- **THEN** the CLI sends `PATCH /api/projects/{projectRef}/labels/catalog/module` carrying only `description`
- **AND** the `supportedValues` of the `module` definition are unchanged

#### Scenario: Update supported values only
- **WHEN** the user runs `mo label update module --supported-values auth,ui,persistence`
- **THEN** the CLI sends `PATCH /api/projects/{projectRef}/labels/catalog/module` carrying only `supportedValues`
- **AND** the `description` of the `module` definition is unchanged

#### Scenario: Update both fields at once
- **WHEN** the user runs `mo label update module --description "..." --supported-values auth,ui`
- **THEN** the CLI sends both `description` and `supportedValues` in the PATCH request
- **AND** the updated definition reflects both changes

#### Scenario: Unknown key fails clearly
- **WHEN** the user runs `mo label update unknown --description "..."`
- **AND** the API responds 404
- **THEN** the CLI prints a clear error stating the key was not found in the project catalog
- **AND** exits with a non-zero status

#### Scenario: Invalid key is rejected before the API call
- **WHEN** the user runs `mo label update Module --description "..."` (uppercase key)
- **THEN** the CLI prints a clear validation error
- **AND** does not send the PATCH request
- **AND** exits with a non-zero status

#### Scenario: Empty supplied description is rejected
- **WHEN** the user runs `mo label update module --description "   "`
- **THEN** the CLI prints a clear validation error stating the description must be non-empty
- **AND** does not send the PATCH request
- **AND** exits with a non-zero status

#### Scenario: System definition modification surfaces the server rejection
- **WHEN** the user runs `mo label update refactor --description "..."`
- **AND** the API rejects modification of the system-origin `refactor` definition
- **THEN** the CLI prints a clear error stating system definitions are immutable
- **AND** the `refactor` definition remains unchanged
- **AND** the CLI exits with a non-zero status
