### Requirement: Disabled buttons are visually unmistakable
Disabled buttons on the issue detail page outside the decision surface MUST be visually distinguishable from the same buttons enabled, at a glance. A disabled button MUST present an unambiguous visual difference from its enabled state, sufficient to be recognizable without interaction.

#### Scenario: Empty-comment submit button renders visibly disabled
- **WHEN** the comment author or comment text is empty so the submit button is disabled
- **THEN** the submit button MUST render visibly disabled and MUST be distinguishable at a glance from its enabled state

#### Scenario: In-flight delete-comment button renders visibly disabled
- **WHEN** a comment deletion is in progress so the delete button is disabled
- **THEN** the delete button MUST render visibly disabled and MUST be distinguishable at a glance from its enabled state
