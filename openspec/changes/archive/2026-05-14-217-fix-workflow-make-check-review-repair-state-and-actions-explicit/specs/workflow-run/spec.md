## ADDED Requirements

### Requirement: check-review-repair-policy

WorkflowRun SHALL be the authoritative source for Check `review-passed` repair policy, including the `fix-review-findings` task id and maximum automatic repair attempts. CheckStageRunner and retry paths SHALL NOT expose or apply a conflicting repair attempt budget for the same Check review gate.

#### Scenario: Failed review schedules repair within budget

- **WHEN** Check stage `review-passed` fails
- **AND** the authoritative repair policy still has remaining attempts
- **THEN** WorkflowRun SHALL schedule or expose `fix-review-findings` as the repair task
- **AND** the repair task SHALL be counted against the authoritative repair budget

#### Scenario: Failed review stops when budget is exhausted

- **WHEN** Check stage `review-passed` fails
- **AND** the authoritative repair budget is exhausted
- **THEN** WorkflowRun SHALL fail the Check stage without scheduling another automatic `fix-review-findings` task
- **AND** the failure SHALL remain traceable to the failed `review-passed` gate

#### Scenario: Retry does not imply another repair

- **WHEN** a failed Check review is retried after the repair budget is exhausted
- **THEN** WorkflowRun MAY reset review/checkpoint work needed for checkpoint recovery
- **AND** it SHALL NOT append another `fix-review-findings` task solely because retry was requested
