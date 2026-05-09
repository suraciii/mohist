## MODIFIED Requirements

### Requirement: REQ-WUI-001 Pipeline UI shows explicit fix tasks

The pipeline UI SHALL render explicit fix tasks from persisted task results. Dynamic fix tasks SHALL be visible even when they are not part of the static stage task definitions.

#### Scenario: Health fix task is visible
- **WHEN** a stage execution contains `fix-build-health`, `fix-check-health`, or `fix-plan-health` in `taskResults`
- **THEN** the task SHALL be displayed in the task list
- **AND** an empty artifact list SHALL NOT hide or invalidate the task

#### Scenario: Review fix task is visible
- **WHEN** a check stage execution contains `fix-review-findings` in `taskResults`
- **THEN** the task SHALL be displayed in the task list
- **AND** its transient output MAY be used for diagnostic display

### Requirement: REQ-WUI-002 Pipeline UI shows repeated check attempts

The pipeline UI SHALL preserve visibility of repeated check results caused by failed check -> fix task -> re-check flows. It SHALL NOT collapse repeated checks in a way that hides the failure or the follow-up verification.

#### Scenario: Re-check is visible
- **WHEN** a check result list contains two results with the same check name around a fix task attempt
- **THEN** the UI SHALL display both check attempts or otherwise distinguish them as separate attempts
- **AND** check evidence SHALL be read from `CheckResult.output` rather than artifact paths
