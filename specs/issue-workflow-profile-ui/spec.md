# OpenSpec Capability: issue-workflow-profile-ui

State-aware rendering of the Issue Detail Workflow Profile card. The card MUST distinguish inherited (reference) profiles from issue-owned (custom) profiles, expose loading and error affordances that are not conflated with the editor, and present custom-profile editing without leaking the editor placeholder into other states. The card SHALL be the single source of truth for workflow profile identity on the Issue Detail page, and the duplicated identity row in the DETAILS sidebar SHALL be removed.

### Requirement: Reference-mode profile shows a read-only summary, not an editor

The Issue Detail Workflow Profile card MUST render a compact read-only summary when the workflow-profile read response has `yaml: null` and `hasCustomTemplate: false`. The card MUST NOT render a YAML textarea, MUST NOT render a `Save` button, and MUST NOT use the editor `Loading workflow profile...` placeholder as the visible state.

#### Scenario: Inherited profile renders summary fields

- **WHEN** the workflow-profile response has `yaml: null` and `hasCustomTemplate: false`
- **THEN** the card shows `Profile` with the `profileId` value
- **AND** shows `Mode` set to `Inherited`
- **AND** shows `Template` describing the inherited source (system default or project default)
- **AND** shows `Overrides` set to `None`
- **AND** exposes a `Customize profile` action that opens the editor

#### Scenario: Inherited profile does not render a textarea

- **WHEN** the workflow-profile response has `yaml: null` and `hasCustomTemplate: false`
- **THEN** the card does not contain a `textarea` element bound to the workflow YAML
- **AND** the card does not contain a `Save` button
- **AND** the editor placeholder `Loading workflow profile...` is not shown

### Requirement: Custom-mode profile shows an editor labeled as issue-owned

The Issue Detail Workflow Profile card MUST render the YAML editor when the workflow-profile response has a non-empty `yaml` and `hasCustomTemplate: true`. The editor MUST be labeled so users can tell it edits issue-owned workflow profile YAML rather than the active workflow run YAML, and the existing save / discard / error / validation behavior MUST remain available.

#### Scenario: Custom profile renders the editor

- **WHEN** the workflow-profile response has `yaml` populated and `hasCustomTemplate: true`
- **THEN** the card renders a YAML editor populated with `yaml`
- **AND** the card label states that the editor edits the issue's own workflow profile YAML
- **AND** existing save, discard, validation error, and unsaved-changes behavior remains available

#### Scenario: Active run YAML is not labeled as profile configuration

- **WHEN** the Issue Detail page exposes active run YAML (for example via the existing `WorkflowYamlDialog`)
- **THEN** the surface labels the YAML as runtime output or observation
- **AND** it does not label active run YAML as workflow profile configuration

### Requirement: Loading state uses a skeleton, not the editor placeholder

While the workflow-profile read request is pending, the Issue Detail Workflow Profile card MUST render a skeleton / loading state that is not the YAML editor placeholder. The card MUST NOT render the editor `Loading workflow profile...` placeholder at any time after the request has resolved.

#### Scenario: Pending request shows skeleton

- **WHEN** the workflow-profile read request is in flight
- **THEN** the card renders a skeleton or loading indicator
- **AND** the card does not render the YAML editor with the `Loading workflow profile...` placeholder text

#### Scenario: Resolved request never shows the editor placeholder

- **WHEN** the workflow-profile read request has resolved (success or failure)
- **THEN** the visible text `Loading workflow profile...` is not present in the Workflow Profile card

### Requirement: Error state shows a compact error block with retry

If the workflow-profile read request fails, the Issue Detail Workflow Profile card MUST render a compact error block describing the failure. If a local retry affordance exists, the card MUST expose it. The card MUST NOT fall back to the editor placeholder in place of the error block.

#### Scenario: Failed request shows an error block

- **WHEN** the workflow-profile read request fails
- **THEN** the card shows a compact error block with the failure message
- **AND** the card does not show a YAML editor with the `Loading workflow profile...` placeholder
- **AND** the card exposes a retry control if a local retry affordance exists

### Requirement: DETAILS sidebar no longer duplicates workflow profile identity

The Issue Detail DETAILS sidebar MUST NOT render a `Workflow Profile` row that duplicates the profile identity already shown by the Workflow Profile card. The sidebar SHALL remain focused on issue metadata such as stage, project, and repository.

#### Scenario: DETAILS sidebar omits workflow profile row

- **WHEN** a user opens an Issue Detail page where the Workflow Profile card is rendered
- **THEN** the DETAILS sidebar does not contain a `Workflow Profile` label/value pair
- **AND** the Workflow Profile card is the single visible source of the issue's workflow profile identity

### Requirement: Issue-owned customization entry point is preserved

The Workflow Profile card SHALL expose a way for users to begin editing issue-owned workflow YAML when the issue is in reference / inherited mode. The entry point MUST be reachable from the reference summary view and MUST be labeled so users know they are switching from an inherited profile to a custom one.

#### Scenario: Reference summary offers a customize action

- **WHEN** the card is showing the reference / inherited summary
- **THEN** the card contains an action labeled in a way that makes the intent clear (such as `Customize profile`)
- **AND** activating the action opens the editor in custom / edit mode

### Requirement: Custom mode allows reverting to the inherited profile

When the Workflow Profile card is in custom mode, the card SHALL provide a way to revert to the inherited profile so the user is not trapped in custom edit mode. The revert affordance MUST be visible only when the issue is currently using a custom profile and MUST be labeled so the user understands the consequence (return to inherited profile).

#### Scenario: Custom editor exposes revert affordance

- **WHEN** the card is in custom mode with `hasCustomTemplate: true`
- **THEN** the card exposes a revert-to-inherited affordance
- **AND** activating the affordance returns the card to the reference / inherited summary state

### Requirement: Web tests cover the four workflow profile states and sidebar de-duplication

Web tests SHALL cover the reference, custom, loading, and error states of the Workflow Profile card, the editor labeling in custom mode, the absence of the editor placeholder outside the pending request, and the absence of the duplicate `Workflow Profile` row in the DETAILS sidebar.

#### Scenario: Reference state tests

- **WHEN** the Web tests render the Workflow Profile card with `yaml: null` and `hasCustomTemplate: false`
- **THEN** the tests verify the card shows the inherited summary fields
- **AND** verify no `textarea` element is rendered for workflow YAML
- **AND** verify no `Save` button is rendered
- **AND** verify the editor placeholder `Loading workflow profile...` is not visible

#### Scenario: Custom state tests

- **WHEN** the Web tests render the Workflow Profile card with `yaml` populated and `hasCustomTemplate: true`
- **THEN** the tests verify the editor is rendered and labeled as issue-owned workflow profile YAML
- **AND** verify the existing save / dirty / validation / unsaved-changes behavior is preserved

#### Scenario: Loading state tests

- **WHEN** the Web tests render the Workflow Profile card while the workflow-profile read request is pending
- **THEN** the tests verify a skeleton / loading indicator is shown
- **AND** verify the `Loading workflow profile...` placeholder is not used

#### Scenario: Error state tests

- **WHEN** the Web tests render the Workflow Profile card after the workflow-profile read request has failed
- **THEN** the tests verify a compact error block is shown
- **AND** verify a retry control is exposed when a local retry affordance exists

#### Scenario: Sidebar de-duplication test

- **WHEN** the Web tests render the Issue Detail page
- **THEN** the tests verify the DETAILS sidebar does not contain a `Workflow Profile` row duplicating the card identity
