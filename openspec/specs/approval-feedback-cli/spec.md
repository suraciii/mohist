# OpenSpec Capability: approval-feedback-cli

### Requirement: CLI provides mo issue feedback list command

The CLI SHALL provide `mo issue feedback list <issue-number>` to list all feedback records for an issue. The command SHALL support `--output json` for machine-readable output.

#### Scenario: List feedback for an issue as table

- **WHEN** the user runs `mo issue feedback list 42`
- **THEN** the output SHALL display a table with feedback id, stage, status, creation date, and a truncated body preview
- **AND** feedback items SHALL be ordered by creation date

#### Scenario: List feedback as JSON

- **WHEN** the user runs `mo issue feedback list 42 --output json`
- **THEN** the output SHALL be a valid JSON array of feedback objects
- **AND** each object SHALL include `id`, `issueNumber`, `workflowRunId`, `stage`, `status`, `body`, `createdAt`, and `resolution`
- **AND** the JSON shape SHALL be stable for agent consumption

#### Scenario: List feedback with stage filter

- **WHEN** the user runs `mo issue feedback list 42 --stage plan`
- **THEN** only feedback records for the `plan` stage SHALL be returned

### Requirement: CLI provides mo issue feedback show command

The CLI SHALL provide `mo issue feedback show <issue-number>` to retrieve a specific feedback record. The command SHALL support `--feedback <id>` to get by id, `--latest` to get the most recent, and `--stage` to filter by stage. The command SHALL support `--output json` for machine-readable output.

#### Scenario: Show feedback by id as JSON

- **WHEN** the user runs `mo issue feedback show 42 --feedback fb_123 --output json`
- **THEN** the output SHALL be a valid JSON object with the full feedback record
- **AND** the object SHALL include `id`, `issueNumber`, `workflowRunId`, `stage`, `status`, `body`, `createdAt`, `resolution`, `resolutionSummary`, and `resolvedAt`
- **AND** the JSON shape SHALL match the agent-readable contract

#### Scenario: Show latest feedback

- **WHEN** the user runs `mo issue feedback show 42 --latest --output json`
- **THEN** the most recently created feedback record SHALL be returned

#### Scenario: Show latest feedback by stage

- **WHEN** the user runs `mo issue feedback show 42 --latest --stage plan --output json`
- **THEN** the most recently created feedback record for the `plan` stage SHALL be returned

#### Scenario: Show feedback requires project context

- **WHEN** the user runs `mo issue feedback show 42 --feedback fb_123`
- **AND** no `--project-id` flag is provided
- **THEN** the CLI SHALL use the current project context
- **AND** the `--project-id` flag SHALL be accepted for explicit project targeting

### Requirement: JSON output schema is stable and compact

The JSON output for feedback commands SHALL follow a stable, compact schema suitable for agent consumption. The schema SHALL be documented and SHALL NOT include extraneous fields.

#### Scenario: Feedback JSON shape

- **WHEN** `mo issue feedback show 42 --feedback fb_123 --output json` is executed
- **THEN** the JSON output SHALL have the shape:
  ```json
  {
    "id": "fb_123",
    "issueNumber": 42,
    "workflowRunId": "wr_...",
    "stage": "plan",
    "status": "open",
    "body": "...user feedback...",
    "createdAt": "...",
    "resolution": null
  }
  ```
- **AND** when resolved, `resolution` SHALL include `resolutionSummary`, `resolvedAt`, and `resolutionTaskId`

#### Scenario: List JSON shape is an array of feedback objects

- **WHEN** `mo issue feedback list 42 --output json` is executed
- **THEN** the output SHALL be a JSON array
- **AND** each element SHALL have the same shape as individual feedback show output

### Requirement: CLI feedback commands use server API

The CLI feedback commands SHALL be thin clients that call the server API. The CLI SHALL NOT contain business logic for feedback creation, resolution, or lifecycle management.

#### Scenario: CLI calls server for feedback data

- **WHEN** `mo issue feedback list` or `mo issue feedback show` is executed
- **THEN** the CLI SHALL call the corresponding server API endpoints
- **AND** the CLI SHALL format and display the server response
- **AND** the CLI SHALL NOT perform any feedback-related business logic

#### Scenario: Server unavailable error

- **WHEN** `mo issue feedback show` is executed
- **AND** the server is not running
- **THEN** the CLI SHALL display "Server is not running. Start with: mo server start"
- **AND** exit with non-zero status
