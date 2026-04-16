## ADDED Requirements

### Requirement: Change directory structure
The system SHALL create a standardized directory structure for each Change under `openspec/changes/{issue-number}-{slug}/`.

**Naming Convention:**
- Base name: `{issue.number}-{slug}`
- Slug: kebab-case version of issue title, max 50 characters
- Conflict resolution: If directory exists, auto-append `-v2`, `-v3`, etc.

#### Scenario: Create new change
- **WHEN** the system initiates a new Change for an issue
- **THEN** it creates the directory `openspec/changes/{issue-number}-{slug}/`
- **AND** it creates the following files:
  - `.change.json` with metadata (name, issue_id, status, order, created_at)
  - `proposal.md` for the change motivation
  - `design.md` for technical approach
  - `specs/` directory for detailed requirements
  - `tasks.json` (generated during plan phase after self-review)
  - `session-memories/` directory for task learnings

#### Scenario: Handle naming conflict
- **WHEN** creating a Change for issue #42 "Add user authentication"
- **AND** `42-add-user-authentication/` already exists
- **THEN** the system creates `42-add-user-authentication-v2/`

### Requirement: Change metadata file
The system SHALL maintain a `.change.json` file with essential Change metadata.

#### Scenario: Read change metadata
- **WHEN** the system needs to access Change information
- **THEN** it reads `openspec/changes/{name}/.change.json`
- **AND** the file contains: name, issue_id, status, created_at

#### Scenario: Update change status
- **WHEN** the Change transitions between stages (planning, reviewing, building, verifying, done)
- **THEN** the system updates the `status` field in `.change.json`
- **AND** the change is persisted to disk

### Requirement: Change archival
The system SHALL support archiving completed Changes.

#### Scenario: Archive completed change
- **WHEN** a Change reaches `done` status
- **THEN** the system moves the directory from `openspec/changes/{name}/` to `openspec/changes/archive/YYYY-MM-DD-{name}/`
- **AND** the session-memories are preserved in the archive
- **AND** the Change remains accessible for historical reference

### Requirement: Issue-Change relationship tracking
The system SHALL track the relationship between Issues and Changes.

#### Scenario: Track multiple changes for one issue
- **WHEN** an issue has multiple Changes over time
- **THEN** the system maintains a list in the issue metadata
- **AND** the list contains all Changes with their status and order
- **AND** users can view the Change history for an issue
