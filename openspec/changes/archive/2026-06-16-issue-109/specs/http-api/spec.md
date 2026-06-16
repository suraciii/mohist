## ADDED Requirements

### Requirement: API provides approval feedback CRUD endpoints

The HTTP API SHALL expose endpoints for creating, listing, and reading approval feedback records.

#### Scenario: Create feedback by requesting changes

- **WHEN** `POST /api/issues/:number/feedback` is called with `{ stage, body }`
- **THEN** the API SHALL create an `ApprovalFeedback` record scoped to the active WorkflowRun and specified stage
- **AND** the API SHALL resume the stage as running and schedule the `apply-feedback` task
- **AND** the response SHALL include the created feedback record with `id`, `stage`, `status`, `body`, and `createdAt`

#### Scenario: Create feedback requires awaiting approval stage

- **WHEN** `POST /api/issues/:number/feedback` is called
- **AND** the current stage is not awaiting approval
- **THEN** the API SHALL return a 409 Conflict response
- **AND** the response SHALL indicate that the stage is not awaiting approval

#### Scenario: List feedback for an issue

- **WHEN** `GET /api/issues/:number/feedback` is called
- **THEN** the response SHALL return all `ApprovalFeedback` records for the issue's active WorkflowRun
- **AND** each record SHALL include `id`, `issueNumber`, `workflowRunId`, `stage`, `status`, `body`, `createdAt`, and `resolution`
- **AND** results SHALL be ordered by `createdAt` descending

#### Scenario: List feedback filtered by stage

- **WHEN** `GET /api/issues/:number/feedback?stage=plan` is called
- **THEN** only feedback records for the `plan` stage SHALL be returned

#### Scenario: Get feedback by id

- **WHEN** `GET /api/issues/:number/feedback/:feedbackId` is called
- **THEN** the response SHALL return the full feedback record
- **AND** the response SHALL include `id`, `issueNumber`, `workflowRunId`, `stage`, `status`, `body`, `createdAt`, `resolutionSummary`, `resolvedAt`, and `resolutionTaskId`

#### Scenario: Feedback not found

- **WHEN** `GET /api/issues/:number/feedback/:feedbackId` is called with an unknown id
- **THEN** the API SHALL return 404

### Requirement: Issue detail response includes approval feedback data

Issue detail and stage-state API responses SHALL include approval feedback data for the active WorkflowRun so clients can render the feedback-resolution trail.

#### Scenario: Issue detail includes feedback history

- **WHEN** `GET /api/issues/:number` is called
- **AND** the active WorkflowRun has `ApprovalFeedback` records
- **THEN** the response SHALL include a `feedback` array with all feedback records
- **AND** each record SHALL include enough data to render the feedback cycle in approval history

#### Scenario: Stage-state includes feedback for the stage

- **WHEN** `GET /api/issues/:number/stage-state` is called
- **AND** the current stage has feedback records
- **THEN** the stage response SHALL include `feedback` with records scoped to that stage
- **AND** the response SHALL distinguish open feedback from resolved feedback

## MODIFIED Requirements

### Requirement: simplified check-stage public model

The HTTP API SHALL expose the simplified CHECK-stage public model for new check-stage runs: `ai-review` as task history, and `review-passed`, `merge-ready`, and `user-approval` as visible checks or approval state. Approval endpoints SHALL validate that the current approval snapshot corresponds to passing review and merge checks for the current worktree snapshot. The approval reject endpoint SHALL create an `ApprovalFeedback` record instead of recording a terminal rejection.

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

#### Scenario: Requesting changes creates feedback not terminal rejection

- **WHEN** a client requests changes at CHECK-stage user approval with feedback body
- **THEN** the API SHALL create an `ApprovalFeedback` record
- **AND** the API SHALL resume the stage as running
- **AND** the API SHALL NOT mark the stage or workflow as failed
- **AND** the response SHALL include the created feedback record
