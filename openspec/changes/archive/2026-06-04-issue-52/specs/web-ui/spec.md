# OpenSpec Capability: web-ui (delta)

This delta adds a new requirement to the existing `web-ui` capability. The full state-aware Workflow Profile card behavior is defined in the new `issue-workflow-profile-ui` capability; this delta records the integration point in the Web UI and the duplicate-identity cleanup in the DETAILS sidebar.

## ADDED Requirements

### Requirement: Issue Detail integrates the state-aware Workflow Profile card

The Issue Detail page SHALL integrate the state-aware Workflow Profile card defined in the `issue-workflow-profile-ui` capability. The Workflow Profile card SHALL be the single source of truth for the issue's workflow profile identity on the Issue Detail page.

#### Scenario: Workflow Profile card is the single source of truth

- **WHEN** a user opens an Issue Detail page
- **THEN** the Workflow Profile card is rendered as part of the page
- **AND** the card follows the state model defined in the `issue-workflow-profile-ui` capability
- **AND** the DETAILS sidebar does not duplicate the workflow profile identity that the card already displays
- **AND** the page keeps the existing `Coder Model` and per-stage override controls in the ACTIONS sidebar unchanged from this change

#### Scenario: Active run YAML is labeled as runtime output

- **WHEN** the Issue Detail page exposes active run YAML
- **THEN** the trigger and dialog label it as active / runtime run YAML
- **AND** explanatory copy does not present the active run YAML as workflow profile configuration
