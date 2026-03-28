## ADDED Requirements

### Requirement: start endpoint uses type-safe enum for error handling
The start endpoint SHALL use `IssueStatus.Blocked` (not a string cast) when setting the issue status on agent failure.

#### Scenario: Agent loop fails
- **WHEN** the agent loop for an issue throws an error
- **THEN** the issue status SHALL be set to `IssueStatus.Blocked` using the enum value
- **AND** no `as any` type assertion SHALL be used

### Requirement: status API uses correct brand name
The status API SHALL use the correct brand name "mo" (not "crawlph") in all user-facing messages.

#### Scenario: No current project
- **WHEN** no current project is selected and a status request is made
- **THEN** the error message SHALL say "mo project use <name>"
- **AND** no occurrence of "crawlph" SHALL appear in the response

### Requirement: API provides operation interface
The pause endpoint SHALL return HTTP 501 for M1, with an explanation that pause is not supported.

#### Scenario: User attempts to pause an issue
- **WHEN** a POST request is made to `/api/issues/:number/pause`
- **THEN** the response SHALL have status code 501
- **AND** the response body SHALL contain an explanation that pause is not supported in M1
