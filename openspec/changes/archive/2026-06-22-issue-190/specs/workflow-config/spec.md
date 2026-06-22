## ADDED Requirements

### Requirement: Built-in workflow profiles include mohist/pr

The profile registry SHALL expose `mohist/pr` as a second built-in workflow profile alongside `mohist/default`. Both profiles SHALL be selectable at project and issue scope through the existing profile-selection mechanism. Profile listing, description, and `suitableFor` surfaces SHALL include `mohist/pr` with the same shape as `mohist/default`. The `mohist/pr` profile SHALL be non-default; selecting no profile SHALL continue to resolve to `mohist/default`.

#### Scenario: Profile registry exposes both built-in profiles

- **WHEN** the profile registry is queried for available profiles
- **THEN** the result SHALL include both `mohist/default` and `mohist/pr`
- **AND** each profile SHALL carry id, display name, description, and `suitableFor`

#### Scenario: mohist/pr is selectable at project and issue scope

- **WHEN** a project or issue selects `mohist/pr` as its workflow profile
- **THEN** the profile manager SHALL resolve workflow loading through `mohist/pr`
- **AND** WorkflowRun execution SHALL use the resolved `mohist/pr` definition

#### Scenario: mohist/default remains the implicit default

- **WHEN** no project or issue workflow profile is selected
- **THEN** the system SHALL continue to use `mohist/default`
- **AND** the presence of `mohist/pr` SHALL NOT change the default-selection rule
