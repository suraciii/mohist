## MODIFIED Requirements

### Requirement: CHECK stage exposes review and merge decisions

The CHECK stage SHALL present one initial user-visible task, `ai-review`, followed by the user-visible checks `review-passed`, `merge-ready`, and `user-approval`. Internal health gates, integration preview evidence, review artifact retries, and implementation-specific validation SHALL NOT be exposed as separate user-facing CHECK-stage checks.

#### Scenario: Check stage starts with ai-review task

- **WHEN** a default CHECK stage starts
- **THEN** the initial user-visible task SHALL be `ai-review`
- **AND** `ai-review` SHALL be represented as task history, not as a check result

#### Scenario: Check stage visible checks are simplified

- **WHEN** CHECK-stage results are presented to users
- **THEN** the visible automated checks SHALL be `review-passed` and `merge-ready`
- **AND** the visible approval point SHALL be `user-approval`
- **AND** users SHALL NOT need to interpret `health:check`, `merge-readiness`, `integration-health-gate-preview`, or `ai-review` as check names

#### Scenario: Internal evidence stays internal

- **WHEN** CHECK-stage execution gathers health, integration-preview, artifact-retry, or repair evidence
- **THEN** that evidence MAY appear in task output, logs, or diagnostic details
- **AND** it SHALL NOT create additional user-visible check-stage decision points
