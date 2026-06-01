## ADDED Requirements

### Requirement: Issue workflow profile YAML API
The HTTP API SHALL expose issue-scoped workflow profile YAML read and save endpoints under the current project and issue context. Saving YAML SHALL parse the submitted YAML into a `WorkflowDefinition`, persist the issue workflow profile snapshot, and return normalized YAML plus refresh-safe metadata.

#### Scenario: Read issue workflow profile YAML
- **WHEN** a client requests `GET /api/issues/:number/workflow/profile/yaml`
- **THEN** the response returns the issue's current normalized workflow profile YAML snapshot
- **AND** the response includes enough metadata for the client to refresh the issue workflow profile state safely

#### Scenario: Save valid issue workflow profile YAML
- **WHEN** a client requests `PUT /api/issues/:number/workflow/profile/yaml` with valid YAML for the current project issue
- **THEN** the server parses the YAML into a `WorkflowDefinition`
- **AND** the server persists the updated `IssueWorkflowProfile` snapshot for that issue
- **AND** the response returns the normalized YAML and enough metadata for the client to refresh safely

#### Scenario: Reject invalid YAML syntax
- **WHEN** a client saves issue workflow profile YAML with invalid YAML syntax
- **THEN** the server returns a validation error response
- **AND** the response clearly indicates that YAML parsing failed
- **AND** the issue workflow profile snapshot is not updated

#### Scenario: Reject invalid workflow shape
- **WHEN** a client saves syntactically valid YAML that does not form a valid `WorkflowDefinition`
- **THEN** the server returns a validation error response
- **AND** the response clearly identifies the invalid workflow shape
- **AND** the issue workflow profile snapshot is not updated
