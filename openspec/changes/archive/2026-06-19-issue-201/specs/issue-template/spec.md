## ADDED Requirements

### Requirement: Issue template schema (frontmatter + sections)

An Issue Template SHALL be defined by a frontmatter block plus an ordered `sections` array. The frontmatter SHALL carry the fields `name`, `about`, `suitable_for`, and `defaults`. The `defaults` field SHALL carry advisory issue defaults (`labels`, `risk`, `workflow`). Each entry in `sections` SHALL carry `title`, `guidance`, and `placeholder`. A template with a missing required field SHALL be rejected as invalid.

#### Scenario: Template frontmatter carries metadata and defaults
- **WHEN** a template is defined
- **THEN** its frontmatter SHALL include `name`, `about`, `suitable_for`, and `defaults`
- **AND** `defaults` SHALL be an advisory bundle of `labels`, `risk`, and `workflow`

#### Scenario: Section carries title, guidance, and placeholder
- **WHEN** a template section is defined
- **THEN** it SHALL include a `title`, a `guidance` block, and a `placeholder`
- **AND** the section's position in the array defines its order in the produced body

#### Scenario: Missing required field is rejected
- **WHEN** a template lacks a required frontmatter field or a section lacks `title`, `guidance`, or `placeholder`
- **THEN** the template SHALL be treated as invalid
- **AND** it SHALL NOT be surfaced as an available template

### Requirement: Issue template is a project-scoped resource

A template SHALL belong to exactly one project, and one project MAY own many templates. A project's available templates SHALL be the union of the built-in default templates (unless disabled by that project) and the project's own custom templates. Custom templates SHALL NOT be shared across projects.

#### Scenario: Available templates combine built-in and custom
- **WHEN** the available templates for a project are resolved
- **THEN** the result SHALL include every built-in default template that the project has not disabled
- **AND** it SHALL include every custom template the project has added

#### Scenario: Custom templates are project-private
- **WHEN** a custom template is added to project A
- **THEN** the template SHALL NOT appear among the available templates of project B

### Requirement: Built-in default template mohist/default

The system SHALL provide a built-in default template identified as `mohist/default`. It SHALL be available to every project unless explicitly disabled. Its `sections` SHALL be, in this exact order: User Voice, Product Shape, Domain Model, Acceptance Criteria, Non-Goals. Each section SHALL carry `guidance` whose content is sourced from the `mohist-explore` skill's writing guidance for that section, and a `placeholder` skeleton matching the three-voice PRD body template.

#### Scenario: Default template is always available
- **WHEN** a project has not disabled the default template
- **THEN** `mohist/default` SHALL appear among the project's available templates
- **AND** its guidance SHALL NOT depend on any project-specific configuration

#### Scenario: Default template section order and guidance source
- **WHEN** the `mohist/default` template is read
- **THEN** its sections SHALL appear in the order User Voice, Product Shape, Domain Model, Acceptance Criteria, Non-Goals
- **AND** each section's `guidance` SHALL reflect the corresponding writing guidance of the `mohist-explore` skill (what to write, what not to write, how to write)

### Requirement: Default template is non-deletable but disableable

The built-in default template SHALL NOT be deletable, so that out-of-the-box usability is guaranteed. A project MAY explicitly disable the default template at the data layer. When disabled, the default template SHALL NOT appear in that project's available templates. Disabling is a data-layer capability only; a dedicated disable UI is out of scope.

#### Scenario: Default template cannot be deleted
- **WHEN** an operation attempts to delete `mohist/default`
- **THEN** the operation SHALL be refused
- **AND** `mohist/default` SHALL remain available to projects that have not disabled it

#### Scenario: Project disables the default template
- **WHEN** a project disables `mohist/default` at the data layer
- **THEN** `mohist/default` SHALL NOT appear in that project's available templates
- **AND** other projects SHALL be unaffected

### Requirement: Section guidance is writing guidance, stripped from the body

A section's `guidance` SHALL define what to write, what not to write, and how to write that section; it is guidance for the agent and the human author, not body content. When an issue body is produced from a template, the `guidance` SHALL be stripped. The produced body SHALL be assembled in section order, using each section's `placeholder` as the skeleton.

#### Scenario: Guidance is not written into the body
- **WHEN** an issue body is produced from a template
- **THEN** the body SHALL contain the section placeholders in order
- **AND** the body SHALL NOT contain any section's `guidance` text

#### Scenario: Body follows template section order
- **WHEN** an issue body is produced from a template
- **THEN** the body sections SHALL appear in the same order as the template's `sections` array

### Requirement: Template suitable_for and isDefault mirror workflow profiles

A template's `suitable_for` SHALL use the same matching semantics as a workflow profile's `suitable_for`, so the two resources can be matched against the same context. A template MAY be marked `isDefault`; the `isDefault` semantics SHALL mirror the workflow profile's default semantics.

#### Scenario: suitable_for matching is shared with workflow profiles
- **WHEN** a template and a workflow profile declare the same `suitable_for` value
- **THEN** both SHALL be matched by the same matching logic against a given context

#### Scenario: isDefault mirrors workflow profile semantics
- **WHEN** a template is marked `isDefault`
- **THEN** it SHALL be treated as the default template for its `suitable_for` context
- **AND** the default-selection rule SHALL be symmetric with the workflow profile default-selection rule

### Requirement: Template list and get API

The system SHALL expose HTTP endpoints to list a project's available templates and to get a single template by name. The list endpoint SHALL return built-in defaults (unless disabled by the project) plus the project's custom templates, each with at least its `name`, `about`, and `suitable_for`. The get endpoint SHALL return the full template including its `sections` with `guidance` and `placeholder`. Create, update, and delete endpoints are out of scope for this change.

#### Scenario: List returns available templates
- **WHEN** the list endpoint is called for a project
- **THEN** it SHALL return every available template (non-disabled defaults plus project customs)
- **AND** each item SHALL include `name`, `about`, and `suitable_for`

#### Scenario: Get returns a single template with full sections
- **WHEN** the get endpoint is called with a template name that exists for the project
- **THEN** it SHALL return the template's frontmatter and its full `sections` array
- **AND** each section SHALL include `title`, `guidance`, and `placeholder`

#### Scenario: Disabled default is excluded from list
- **WHEN** a project has disabled `mohist/default`
- **AND** the list endpoint is called for that project
- **THEN** the response SHALL NOT include `mohist/default`

### Requirement: Template list and get CLI

The CLI SHALL provide `mo issue template list` to list the current project's available templates and `mo issue template get <name>` to display a single template. Both commands SHALL consume the template list/get API.

#### Scenario: mo issue template list
- **WHEN** `mo issue template list` is run against a project
- **THEN** it SHALL list the project's available templates (non-disabled defaults plus customs)
- **AND** each entry SHALL identify the template by name

#### Scenario: mo issue template get
- **WHEN** `mo issue template get <name>` is run with a name that exists for the project
- **THEN** it SHALL display the template's metadata and its sections
- **AND** it SHALL show each section's `guidance`

### Requirement: Web UI template selector prefills body skeleton

The create-issue dialog SHALL present a template selector that consumes the list API. Selecting a template SHALL prefill the issue body with a section skeleton assembled in section order from each section's `placeholder`. The prefill SHALL NOT inject any section's `guidance` into the body.

#### Scenario: Selector lists available templates
- **WHEN** the create-issue dialog is opened
- **THEN** the template selector SHALL populate from the list API
- **AND** it SHALL list the project's available templates (non-disabled defaults plus customs)

#### Scenario: Selecting a template prefills the body skeleton
- **WHEN** a user selects a template in the selector
- **THEN** the issue body SHALL be prefilled with a section skeleton in section order
- **AND** the skeleton SHALL use each section's `placeholder`
- **AND** no section's `guidance` SHALL appear in the prefilled body

### Requirement: Project can add custom templates at the data layer

A project SHALL be able to add custom templates so it can define its own issue writing conventions. The entry mechanism (project configuration file or API) is implementation-defined. A custom template that is valid SHALL be surfaced in list and get results alongside the non-disabled defaults. Full CRUD UX (template editor, preview, version management) is out of scope for this change.

#### Scenario: Valid custom template is surfaced
- **WHEN** a project adds a valid custom template via the supported entry mechanism
- **THEN** the template SHALL appear in the project's list and get results
- **AND** it SHALL be usable by the Web UI selector and the CLI

#### Scenario: Custom template entry mechanism is implementation-defined
- **WHEN** a project adds a custom template
- **THEN** the system MAY accept it via a project configuration file or via an API
- **AND** it SHALL NOT be required to provide a dedicated editing UI in this change
