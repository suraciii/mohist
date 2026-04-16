## ADDED Requirements

### Requirement: Project-level specs directory
The system SHALL store Change specs in a project-level directory that is version controlled.

#### Scenario: Store specs in project
- **WHEN** creating a new Change
- **THEN** the system creates `openspec/changes/{name}/` under the project root
- **AND** the directory is not in .gitignore by default
- **AND** specs are committed with code changes

### Requirement: Specs in code review
The system SHALL ensure specs are visible during code review.

#### Scenario: Review includes specs
- **WHEN** a developer opens a PR
- **THEN** the PR includes `openspec/changes/{name}/` files
- **AND** reviewers can see proposal.md, design.md, and specs/
- **AND** reviewers can comment on specific requirements
- **AND** reviewers can verify implementation matches specs

### Requirement: Specs location configuration
The system SHALL support configuration of the specs storage location.

#### Scenario: Configure specs location
- **WHEN** initializing mohist in a project
- **THEN** the system creates `.mohist/config.yaml` with:
  ```yaml
  specs:
    location: "project"  # or ".mohist"
    project_path: "openspec"
    git_track: true
  ```
- **AND** the project can customize the path

### Requirement: Specs archival
The system SHALL support archiving completed specs while preserving history.

#### Scenario: Archive completed change specs
- **WHEN** a Change is completed
- **THEN** the system moves the directory to `openspec/changes/archive/YYYY-MM-DD-{name}/`
- **AND** the archived specs remain in git history
- **AND** active development directory remains clean

### Requirement: Backward compatibility
The system SHALL support legacy mode where specs are stored in `.mohist/` for existing projects.

#### Scenario: Legacy project migration
- **WHEN** an existing project uses `.mohist/changes/`
- **THEN** the system continues to work with the legacy location
- **AND** provides a migration command to move to project-level storage
