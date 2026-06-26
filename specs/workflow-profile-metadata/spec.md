## ADDED Requirements

### Requirement: Workflow profile YAML supports a multi-line description field

Workflow profile YAML documents SHALL accept an optional top-level `description` field whose value is a multi-line natural-language string (YAML block scalar `|`). The `description` field SHALL describe the profile's intended use, scope, typical behavior, and exclusions in terms readable by both humans and AI.

#### Scenario: Profile with description block scalar
- **WHEN** a workflow profile YAML defines `description: |` followed by indented multi-line text
- **THEN** the system SHALL parse the description as a single multi-line string preserving internal line breaks

#### Scenario: Profile without description field
- **WHEN** a workflow profile YAML omits the `description` field
- **THEN** the system SHALL use the default fallback description `"No description provided"`

#### Scenario: Description is not executed
- **WHEN** the workflow engine executes a profile
- **THEN** the `description` field SHALL be treated as passive metadata
- **AND** it SHALL NOT affect stage execution, task dispatch, or check behavior

### Requirement: Server exposes profile metadata through the profile info model

The server SHALL include the full multi-line `description` in `WorkflowProfileInfo` returned by `IssueWorkflowProfileRegistry.List()`. The description SHALL preserve line breaks from the source YAML.

#### Scenario: Profile list includes description
- **WHEN** the server lists available workflow profiles
- **THEN** each entry SHALL include `id`, `displayName`, `description`, and `isDefault`
- **AND** `description` SHALL be the full multi-line text from the YAML

#### Scenario: Profile with only default fallback description
- **WHEN** a profile has no `description` in its YAML
- **AND** its metadata is queried
- **THEN** the returned description SHALL be `"No description provided"`

### Requirement: Default profile has a complete AI-readable description

The `mohist/local` workflow profile YAML SHALL include a `description` field that covers: the profile's intended scope (full feature implementation with plan/design/build/check/integrate stages), its approval requirements, and explicit exclusions (simple bug fixes, experiments, pure refactoring).

#### Scenario: Default profile description is present
- **WHEN** the `mohist-local.workflow.yaml` file is inspected
- **THEN** it SHALL contain a `description` field
- **AND** the description SHALL use YAML block scalar format
- **AND** the description SHALL describe the full pipeline: plan, design, build, check, integrate
- **AND** the description SHALL note that it is not suitable for simple fixes, experiments, or pure refactoring

### Requirement: System profile catalog exposes only executable profiles

The system SHALL expose a workflow profile only when its metadata and execution definition describe the same behavior. It SHALL NOT list metadata-only variants that reuse `mohist/local` while promising a lighter or different workflow.

#### Scenario: default profile is listed
- **WHEN** the system lists available profiles
- **THEN** the `mohist/local` profile SHALL be present

#### Scenario: unimplemented profiles are not listed
- **WHEN** a profile such as `mohist/quick-fix` or `mohist/experiment` has no distinct executable definition
- **THEN** it SHALL NOT be returned by the system profile catalog
- **AND** it SHALL NOT be resolvable as a system template

### Requirement: Profile metadata is backward compatible

Existing workflow profile consumers SHALL continue to work without modification. The `description` field SHALL be the only new top-level field. The `stages` key and all stage/task/check definitions SHALL remain unchanged.

#### Scenario: Profile with only description added still loads
- **WHEN** a workflow profile YAML has only `description` added above existing `stages`
- **THEN** the workflow engine SHALL parse and execute the profile identically to before the change

#### Scenario: Other metadata fields are absent
- **WHEN** a workflow profile YAML is inspected
- **THEN** the top-level SHALL NOT contain `risk_level`, `typical_duration`, `suitable_for`, `avoid_for`, `tags`, or `default_approval_policy`
