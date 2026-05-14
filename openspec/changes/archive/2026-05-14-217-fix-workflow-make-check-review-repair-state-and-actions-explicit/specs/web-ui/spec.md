## ADDED Requirements

### Requirement: check-review-repair-surface

Issue Detail SHALL present Check review repair state as a user-facing decision surface when a Check review failure has repair evidence. The surface SHALL distinguish repair task outcome from review gate verdict and SHALL present checkpoint retry, review-only rerun, and fixing review findings as separate user intents.

#### Scenario: Check repair state is visible

- **WHEN** a user views an issue blocked by Check review failure
- **AND** stage-state includes `checkRepair`
- **THEN** Issue Detail SHALL show auto-fix status, attempts used and remaining, last repair status, follow-up review status, and stop reason
- **AND** it SHALL show unresolved review summary when available

#### Scenario: Completed repair followed by failed review is not contradictory

- **WHEN** the last `fix-review-findings` task completed
- **AND** the follow-up `review-passed` check failed
- **THEN** Issue Detail SHALL state that the last repair completed and the follow-up review failed
- **AND** it SHALL NOT present repair completion as review gate success

#### Scenario: Repair exhaustion explains next action

- **WHEN** `checkRepair` reports zero remaining automatic repair attempts
- **THEN** Issue Detail SHALL explain that auto-fix will not continue automatically
- **AND** it SHALL recommend a clear next action such as manual takeover or review-only rerun after code changes

#### Scenario: Recovery actions use explicit intent labels

- **WHEN** Check review repair state is shown
- **THEN** Issue Detail SHALL label actions by intent, including `Retry checkpoint`, `Rerun review only`, and `Fix review findings` when available
- **AND** ambiguous `Retry` SHALL NOT be the primary action label for review repair failures

### Requirement: check-review-repair-regressions

Check review repair behavior SHALL have regression coverage that protects both the structured API state and the user-facing display semantics.

#### Scenario: Backend repair projection is covered

- **WHEN** backend tests create Check state with completed repair tasks and failed follow-up review
- **THEN** tests SHALL verify attempts, repair availability, last repair status, follow-up review status, and stop reason in stage-state

#### Scenario: Exhausted retry does not look like repair

- **WHEN** backend tests retry an exhausted failed Check review
- **THEN** tests SHALL verify no new `fix-review-findings` task is scheduled

#### Scenario: Frontend display semantics are covered

- **WHEN** frontend tests render completed repair plus failed follow-up review
- **THEN** tests SHALL verify the UI displays repair-attempt-completed plus review-still-failing semantics
- **AND** tests SHALL verify exhausted repair budget guidance and explicit action labels
