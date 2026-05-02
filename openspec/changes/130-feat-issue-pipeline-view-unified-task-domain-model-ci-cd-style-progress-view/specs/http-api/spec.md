## ADDED Requirements

### Requirement: API provides stage executions endpoint

Server SHALL provide `GET /api/issues/:number/executions` endpoint that returns all stage execution records for the specified issue. The response SHALL include structured `StageTaskResult[]` and `CheckResult[]` for each execution.

#### Scenario: Get executions for active issue

- **WHEN** CLI requests `GET /api/issues/1/executions`
- **AND** issue #1 has completed Plan (passed) and is in Build (running)
- **THEN** the response returns HTTP 200 with body:
```json
{
  "data": [
    {
      "id": "uuid-1",
      "stage": "plan",
      "status": "passed",
      "taskResults": [
        { "taskId": "proposal", "title": "proposal.md", "status": "completed", "artifacts": ["proposal.md"], "attempts": 1, "duration": 45000 },
        { "taskId": "specs", "title": "specs/", "status": "completed", "artifacts": ["specs/"], "attempts": 1, "duration": 32000 }
      ],
      "checkResults": [
        { "name": "proposal-complete", "status": "pass" },
        { "name": "user-approval", "status": "pass" }
      ],
      "createdAt": "2026-05-01T10:00:00.000Z",
      "updatedAt": "2026-05-01T10:15:00.000Z"
    },
    {
      "id": "uuid-2",
      "stage": "build",
      "status": "running",
      "taskResults": [
        { "taskId": "T-001", "title": "Add auth module", "status": "completed", "artifacts": [], "attempts": 1, "duration": 120000 }
      ],
      "checkResults": [],
      "createdAt": "2026-05-01T10:15:00.000Z",
      "updatedAt": "2026-05-01T10:20:00.000Z"
    }
  ]
}
```

#### Scenario: Get executions for issue with escalation cycle

- **WHEN** issue #2 has the history: Plan → Build → Check(fail) → Plan(retry) → Build → Check(pass)
- **THEN** `GET /api/issues/2/executions` returns 5 execution records
- **AND** records are ordered by `createdAt` ascending
- **AND** includes both the initial and retry Plan executions with their respective task results

#### Scenario: Issue not found

- **WHEN** CLI requests `GET /api/issues/999/executions`
- **AND** issue #999 does not exist
- **THEN** the response returns HTTP 404 with error message "Issue not found"

#### Scenario: Issue with no executions

- **WHEN** CLI requests `GET /api/issues/1/executions`
- **AND** issue #1 is in draft stage with no executions
- **THEN** the response returns HTTP 200 with `{ "data": [] }`
