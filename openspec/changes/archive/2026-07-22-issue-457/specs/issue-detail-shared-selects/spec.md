### Requirement: Shared select component everywhere
Every dropdown/select control rendered on the issue detail page MUST use the shared select component. Native HTML `<select>` elements MUST NOT be rendered on the issue detail page.

#### Scenario: Sessions status filter uses the shared select
- **WHEN** the sessions panel is rendered with one or more sessions
- **THEN** the status filter MUST be the shared select component, not a native `<select>`

#### Scenario: Sessions stage filter uses the shared select
- **WHEN** the sessions panel is rendered with one or more sessions
- **THEN** the stage filter MUST be the shared select component, not a native `<select>`

#### Scenario: Sessions sort control uses the shared select
- **WHEN** the sessions panel is rendered with one or more sessions
- **THEN** the sort control MUST be the shared select component, not a native `<select>`
