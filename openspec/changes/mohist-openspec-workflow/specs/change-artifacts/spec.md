## ADDED Requirements

### Requirement: Change directory structure
The system SHALL create a standardized directory structure for each Change under `.mohist-specs/changes/{change-name}/`.

#### Scenario: Create new change
- **WHEN** the system initiates a new Change for an issue
- **THEN** it creates the directory `.mohist-specs/changes/{change-name}/`
- **AND** it creates the following files:
  - `.change.json` with metadata (name, issue_id, status, order, created_at)
  - `proposal.md` for the change motivation
  - `design.md` for technical approach
  - `specs/` directory for detailed requirements
  - `prd.json` (generated during review phase)

### Requirement: Change metadata file
The system SHALL maintain a `.change.json` file with essential Change metadata.

#### Scenario: Read change metadata
- **WHEN** the system needs to access Change information
- **THEN** it reads `.mohist-specs/changes/{name}/.change.json`
- **AND** the file contains: name, issue_id, status, order, created_at, prd_generated

#### Scenario: Update change status
- **WHEN** the Change transitions between stages (planning, reviewing, building, verifying, done)
- **THEN** the system updates the `status` field in `.change.json`
- **AND** the change is persisted to disk

### Requirement: Change archival
The system SHALL support archiving completed Changes.

#### Scenario: Archive completed change
- **WHEN** a Change reaches `done` status
- **THEN** the system moves the directory from `.mohist-specs/changes/{name}/` to `.mohist-specs/archive/YYYY-MM-DD-{name}/`
- **AND** the Change remains accessible for historical reference

### Requirement: Issue-Change relationship tracking
The system SHALL track the relationship between Issues and Changes.

#### Scenario: Track multiple changes for one issue
- **WHEN** an issue has multiple Changes over time
- **THEN** the system maintains `.mohist/issues/{id}/changes.json`
- **AND** the file contains a list of all Changes with their status and order
