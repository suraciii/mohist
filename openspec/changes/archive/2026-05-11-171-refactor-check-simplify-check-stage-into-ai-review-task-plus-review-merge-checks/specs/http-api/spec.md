## MODIFIED Requirements

### Requirement: simplified check-stage public model

The HTTP API SHALL expose the simplified CHECK-stage public model for new check-stage runs: `ai-review` as task history, and `review-passed`, `merge-ready`, and `user-approval` as visible checks or approval state. Approval endpoints SHALL validate that the current approval snapshot corresponds to passing review and merge checks for the current worktree snapshot.

#### Scenario: Issue detail exposes simplified checks

- **WHEN** a client requests `GET /api/issues/:number` for an issue in or after a new CHECK-stage run
- **THEN** the response SHALL expose CHECK-stage visible checks named `review-passed`, `merge-ready`, and `user-approval`
- **AND** it SHALL NOT require clients to interpret `health:check`, `merge-readiness`, `integration-health-gate-preview`, or `ai-review` as visible check names

#### Scenario: Check suite endpoint exposes simplified checks

- **WHEN** a client requests `GET /api/issues/:number/check-suite` for a new CHECK-stage run
- **THEN** the active check suite SHALL contain `review-passed`, `merge-ready`, and `user-approval` check state
- **AND** it SHALL NOT initialize `ai-review` as a check state key for new runs

#### Scenario: Approval validates current reviewed merge-ready snapshot

- **WHEN** a client approves CHECK-stage user approval
- **THEN** the API SHALL require `review-passed` to be passed for the approval snapshot
- **AND** it SHALL require `merge-ready` to be passed for the approval snapshot
- **AND** it SHALL reject approval if current `HEAD`, worktree cleanliness, or approval snapshot no longer matches the passed review and merge state
