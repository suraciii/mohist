## ADDED Requirements

### Requirement: Workflows profile detail resolves via literal path

The Web UI SHALL request a workflow profile detail using the profile id as literal path segments. When a profile id contains `/` (e.g. `mohist/default`), the request path SHALL be `/workflow-templates/system/mohist/default` and SHALL NOT URL-encode the `/` into `%2F`, so the backend `{*id}` catch-all route matches.

#### Scenario: Open profile card shows stages and YAML

- **WHEN** a user clicks a workflow profile card whose id is `mohist/default`
- **THEN** the Web UI issues `GET /api/workflow-templates/system/mohist/default` with the slash left literal (not `%2F`)
- **AND** the profile detail renders its stages list and YAML definition

#### Scenario: All profiles back navigation still works

- **WHEN** a user is viewing a workflow profile detail and activates the existing "All profiles" control
- **THEN** the Web UI returns to the profiles list
- **AND** no profile-detail URL encoding regression is introduced

### Requirement: Settings main content area renders only tab navigation

The Settings page main content area SHALL render only tab navigation. It SHALL NOT render a page-level title (e.g. `<h1>Settings</h1>`) or a New Issue control in the main content area; those global-navigation affordances belong to the sidebar.

#### Scenario: No duplicate header above tabs

- **WHEN** a user opens any Settings tab route
- **THEN** the main content area shows tab navigation as the topmost header element
- **AND** no `Settings` page title is rendered in the main content area
- **AND** no `New Issue` control is rendered in the main content area

#### Scenario: Sidebar global navigation unchanged

- **WHEN** a user views the Settings page
- **THEN** the left sidebar continues to render its global navigation (toggle sidebar, New Issue, Settings entry)
- **AND** the sidebar's New Issue affordance remains available and unchanged

#### Scenario: Tab switches do not reintroduce a header

- **WHEN** a user switches between the six Settings tabs
- **THEN** no per-tab page title or New Issue header is introduced in the main content area

### Requirement: Settings mutation feedback uses sonner toasts

Settings section mutations SHALL surface success and failure feedback via sonner toasts (`toast.success` / `toast.error`). Inline field-level validation errors SHALL remain rendered inline beneath the offending field and SHALL NOT be converted to toasts.

#### Scenario: Repository mutations surface toasts

- **WHEN** a user adds, removes, or sets a default repository
- **THEN** the Web UI emits a `toast.success` on success or a `toast.error` on failure

#### Scenario: Template mutations surface toasts

- **WHEN** a user overrides, resets, or deletes a project template
- **THEN** the Web UI emits a `toast.success` on success or a `toast.error` on failure

#### Scenario: System log level change no longer fails silently

- **WHEN** a user changes the system log level
- **THEN** the Web UI emits a `toast.success` on success
- **AND** emits a `toast.error` on failure instead of failing silently

#### Scenario: Coder agent model change invokes the toast feedback path

- **WHEN** a user selects a coder agent model
- **THEN** the Web UI invokes the toast feedback path on success or failure

#### Scenario: Field-level validation errors stay inline

- **WHEN** the Runtime form has field-level validation errors
- **THEN** each error SHALL render inline (red text) beneath the offending field
- **AND** SHALL NOT be surfaced as a toast

### Requirement: Coder Agent section omits redundant runtime summary

The Coder Agent Settings section SHALL NOT render the Runtime/Command/Models summary block. The default coder agent model selector and stage model overrides SHALL remain available.

#### Scenario: Runtime summary block removed

- **WHEN** a user opens the Coder Agent Settings tab
- **THEN** the section does not render a Runtime/Command/Models three-column summary block

#### Scenario: Model controls remain

- **WHEN** a user opens the Coder Agent Settings tab
- **THEN** the default coder agent model selector remains available
- **AND** the stage model overrides remain available

#### Scenario: Optional lightweight model count hint

- **WHEN** the Coder Agent section renders the model selector
- **THEN** it MAY show a lightweight `N models available` hint near the selector to replace the removed Models count

### Requirement: Runtime form unsupportedFields mechanism preserved

The Runtime form SHALL preserve its `unsupportedFields` backward-compatibility mechanism. The mutation-feedback unification and the other Settings changes SHALL NOT alter this path.

#### Scenario: Unsupported field is disabled, not erroring

- **WHEN** the server does not return a given Runtime form field
- **THEN** that field SHALL render disabled in the UI
- **AND** the form SHALL NOT throw or surface an error for the absent field

#### Scenario: Toast refactor leaves unsupportedFields path intact

- **WHEN** Settings mutation feedback is unified to toasts
- **THEN** the `unsupportedFields` field-disabling behavior remains unchanged
