## MODIFIED Requirements

### Requirement: REQ-API-001 Approval endpoints require current-stage awaiting approval

Approval and rejection endpoints SHALL accept only approvals whose `approvalState.stage` equals the issue's current stage and whose status is `awaiting`.

#### Scenario: Stale awaiting approval rejected
- **GIVEN** an issue is in `check` stage
- **AND** `approvalState.stage` is `plan`
- **AND** `approvalState.status` is `awaiting`
- **WHEN** the client calls `POST /api/issues/:number/approve`
- **THEN** the API SHALL reject the request as having no current pending approval

#### Scenario: Plan approval resumes pipeline
- **GIVEN** an issue is awaiting current-stage Plan approval
- **WHEN** the client calls `POST /api/issues/:number/approve`
- **THEN** the API SHALL mark Plan approval approved
- **AND** enqueue pipeline resume
- **AND** return a message describing resume/build behavior

### Requirement: REQ-API-002 Check approval queues merge

Check approval SHALL enqueue the issue into the merge queue instead of reporting the issue as done.

#### Scenario: Check approval enqueues merge
- **GIVEN** an issue is awaiting current-stage Check approval
- **WHEN** the client calls `POST /api/issues/:number/approve`
- **THEN** the API SHALL mark Check approval approved
- **AND** call the merge queue enqueue path
- **AND** return a message describing queued-for-merge behavior

### Requirement: REQ-API-003 False-done state is visible to API consumers

API responses that include issue data SHALL preserve enough raw state for consumers to identify false-done issues: `stage`, `status`, and `mergeState`.

#### Scenario: Done without merged state remains distinguishable
- **GIVEN** an issue has `stage=done`
- **AND** `status=completed`
- **AND** `mergeState` is null or not `merged`
- **WHEN** the issue is returned by an API endpoint
- **THEN** the response SHALL include the non-merged or null merge state
- **AND** clients SHALL be able to classify it as false-done
