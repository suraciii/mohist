## ADDED Requirements

### Requirement: advance_stage tool enforces M1 stage transition whitelist
The advance_stage tool SHALL only allow stage transitions defined in the M1 whitelist: `draft→designing`, `designing→implementing`, `implementing→done`. All other transitions SHALL be rejected with an error message listing allowed transitions from the current stage.

#### Scenario: Valid M1 transition
- **WHEN** LLM calls advance_stage with issue in stage "designing" and target stage "implementing"
- **THEN** the issue stage SHALL be updated to "implementing"
- **AND** a success message SHALL be returned

#### Scenario: Invalid transition — skip stage
- **WHEN** LLM calls advance_stage with issue in stage "designing" and target stage "done"
- **THEN** the transition SHALL be rejected
- **AND** an error message SHALL be returned listing allowed transitions from "designing"

#### Scenario: Invalid transition — backward
- **WHEN** LLM calls advance_stage with issue in stage "implementing" and target stage "draft"
- **THEN** the transition SHALL be rejected
- **AND** an error message SHALL be returned

#### Scenario: Invalid transition — to waiting stage
- **WHEN** LLM calls advance_stage with issue in stage "designing" and target stage "waiting-design-review"
- **THEN** the transition SHALL be rejected
- **AND** an error message SHALL be returned
