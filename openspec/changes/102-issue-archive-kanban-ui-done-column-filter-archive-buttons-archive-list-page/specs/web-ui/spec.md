## ADDED Requirements

### Requirement: Router supports /archived route

The application router SHALL include a `/archived` route rendering the archived issues list page.

#### Scenario: Navigate to /archived

- **WHEN** user navigates to `/archived`
- **THEN** the archived issues list page is rendered inside the ProjectGuard layout

### Requirement: API client provides archive methods

`api.ts` SHALL provide `archiveIssue`, `unarchiveIssue`, `archiveAllCompleted`, and `getArchivedIssues` methods corresponding to the backend archive endpoints from Issue #101.

#### Scenario: archiveIssue call

- **WHEN** calling `api.archiveIssue(42)`
- **THEN** a `POST /api/issues/42/archive` request is sent
- **AND** the updated issue is returned

#### Scenario: unarchiveIssue call

- **WHEN** calling `api.unarchiveIssue(42)`
- **THEN** a `POST /api/issues/42/unarchive` request is sent
- **AND** the updated issue is returned

#### Scenario: archiveAllCompleted call

- **WHEN** calling `api.archiveAllCompleted()`
- **THEN** a `POST /api/issues/archive-completed` request is sent
- **AND** the result includes the count of archived issues

#### Scenario: getArchivedIssues call

- **WHEN** calling `api.getArchivedIssues()`
- **THEN** a `GET /api/issues?archived=true` request is sent
- **AND** an array of archived issues is returned

### Requirement: Issue type includes archivedAt field

The `Issue` interface in `types.ts` SHALL include an optional `archivedAt` field.

#### Scenario: Issue with archivedAt

- **WHEN** the backend returns an issue with `archived_at` set
- **THEN** the `Issue.archivedAt` field contains the ISO date string

#### Scenario: Issue without archivedAt

- **WHEN** the backend returns an issue without `archived_at`
- **THEN** the `Issue.archivedAt` field is `undefined`
