## ADDED Requirements

### Requirement: Settings save and reset failures surface inline to the user

Settings sections that perform save or reset operations SHALL surface failures to the user inline within the section. A failure handler SHALL NOT swallow the error with an empty `catch {}` block. A critical failure (one that leaves the form unsaved or the configuration in an unintended state) SHALL be reported as inline feedback within the section, not exclusively as a transient toast.

#### Scenario: Save failure surfaces inline in Agent settings

- **WHEN** the Agent settings `handleSave` operation fails
- **THEN** the failure SHALL be reported inline within the Agent settings section (e.g. via `saveError`)
- **AND** the failure SHALL NOT be swallowed by an empty `catch {}` block

#### Scenario: Reset failure surfaces inline in Agent settings

- **WHEN** the Agent settings `confirmReset` operation fails
- **THEN** the failure SHALL be reported inline within the Agent settings section
- **AND** the failure SHALL NOT be swallowed by an empty `catch {}` block

#### Scenario: Critical failure is not toast-only

- **WHEN** a settings save or reset fails critically (form left unsaved or configuration in unintended state)
- **THEN** the section SHALL render inline error feedback that persists until addressed
- **AND** the error SHALL NOT be communicated solely through a transient toast notification

### Requirement: Settings field errors are exposed to assistive technology

Where a Settings section renders inline field errors, the error message SHALL be associated to its input via `aria-describedby` referencing the error element, and the input SHALL carry `aria-invalid` while the error is present. This SHALL apply at minimum to Agent settings inputs (including `InputField`) and Label-catalog create/edit/delete fields, matching the existing `PreferencesSection` pattern.

#### Scenario: Agent input fields associate errors via aria-describedby and aria-invalid

- **WHEN** an Agent settings input (including `InputField`) has an inline validation error
- **THEN** the input element SHALL carry `aria-invalid`
- **AND** the input element SHALL reference the error message element via `aria-describedby`

#### Scenario: Label catalog fields associate errors via aria-describedby and aria-invalid

- **WHEN** a Label-catalog create, edit, or delete field has an inline error
- **THEN** the corresponding input element SHALL carry `aria-invalid`
- **AND** the input element SHALL reference the error message element via `aria-describedby`

### Requirement: A dirty Settings form warns before tab switches discard unsaved changes

When the active Settings section holds a form in a dirty (unsaved) state, switching to another Settings tab SHALL prompt the user to confirm the switch rather than silently discarding the changes. The guard SHALL be implemented for `AgentSettingsSection` first and SHALL cover navigation initiated through the Settings sub-navigation.

#### Scenario: Dirty Agent form warns before tab switch

- **WHEN** the Agent settings form is dirty (unsaved changes present)
- **AND** the user selects another Settings sub-navigation item
- **THEN** the user SHALL be prompted to confirm the tab switch before navigation proceeds
- **AND** the unsaved changes SHALL NOT be silently discarded

#### Scenario: Clean Agent form switches tabs without prompting

- **WHEN** the Agent settings form has no unsaved changes
- **AND** the user selects another Settings sub-navigation item
- **THEN** the tab switch SHALL proceed without a confirmation prompt
