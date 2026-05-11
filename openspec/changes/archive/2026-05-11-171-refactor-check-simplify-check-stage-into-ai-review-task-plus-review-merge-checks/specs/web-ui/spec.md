## MODIFIED Requirements

### Requirement: simplified check-stage display

The Web UI and CLI SHALL present the CHECK stage using the simplified public model. Users SHALL see `ai-review` as work being performed and SHALL see only `review-passed`, `merge-ready`, and approval as CHECK-stage decision points.

#### Scenario: UI shows ai-review as task

- **WHEN** a user views CHECK-stage progress for a new run
- **THEN** the UI SHALL show `ai-review` as task work or task history
- **AND** it SHALL NOT show `ai-review` as a check decision point

#### Scenario: UI shows simplified checks

- **WHEN** a user views CHECK-stage checks for a new run
- **THEN** the UI SHALL show review result and merge readiness using the `review-passed` and `merge-ready` check states
- **AND** it SHALL show the approval state separately as `user-approval`

#### Scenario: UI hides internal check names

- **WHEN** CHECK-stage progress or done evidence is rendered
- **THEN** the UI SHALL NOT require the user to understand `health:check`, `integration-health-gate-preview`, or `merge-readiness`
- **AND** any related diagnostic evidence SHALL be presented as supporting details rather than primary checks
