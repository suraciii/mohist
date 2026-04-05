## ADDED Requirements

### Requirement: Project selector in header
The system SHALL display a project selector dropdown in the header that allows switching between projects.

#### Scenario: Switch project
- **WHEN** user selects a different project from the dropdown
- **THEN** the kanban and all data refresh to show the selected project's issues
- **AND** the SSE connection reconnects with the new `projectId` parameter

#### Scenario: Single project
- **WHEN** only one project exists
- **THEN** the project selector displays the project name without a dropdown

### Requirement: Project data isolation
All API requests and SSE events SHALL be scoped to the currently selected project. SSE events include `projectId` for client-side filtering, and the SSE endpoint supports `?projectId=xxx` for server-side filtering.

#### Scenario: Events from other projects
- **WHEN** an issue in project B changes while viewing project A
- **THEN** the SSE connection (filtered by projectId=A) does not receive project B events

#### Scenario: Issue list scoped to project
- **WHEN** user views the kanban for project A
- **THEN** only project A's issues are displayed

### Requirement: SSE reconnection on project switch
The system SHALL close the current SSE connection and create a new one with the updated `projectId` query parameter when the user switches projects.

#### Scenario: Project switch reconnects SSE
- **WHEN** user switches from project A to project B
- **THEN** the SSE connection to `/api/events?projectId=A` is closed
- **AND** a new SSE connection to `/api/events?projectId=B` is established
