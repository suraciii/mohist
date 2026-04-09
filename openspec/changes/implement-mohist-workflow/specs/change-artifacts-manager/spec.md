## ADDED Requirements

### Requirement: Change artifacts directory structure
The system SHALL create and manage a standardized directory structure for change artifacts.

#### Scenario: Create change directory
- **WHEN** a new change is initiated for issue #42
- **THEN** the system SHALL create a directory at `.mohist/changes/42-{slug}/`
- **AND** the directory SHALL contain subdirectories for specs

#### Scenario: Artifacts directory layout
- **GIVEN** a change directory exists
- **THEN** the system SHALL organize artifacts as follows:
  - `proposal.md`: Change proposal document
  - `design.md`: Technical design document
  - `specs/`: Directory containing capability specifications
  - `prd.json`: Product requirements document with tasks

### Requirement: Git integration for artifacts
All change artifacts SHALL be tracked in Git alongside the codebase.

#### Scenario: Artifacts committed to Git
- **WHEN** a change artifact is created or updated
- **THEN** the artifact SHALL be stored in the Git repository
- **AND** the artifact SHALL be committed with the associated code changes

#### Scenario: Git attributes for artifacts
- **GIVEN** artifacts are stored in `.mohist/changes/`
- **THEN** the system SHALL recommend adding `.gitattributes` entries
- **AND** mark artifacts as `linguist-generated` to reduce review noise

### Requirement: Artifact versioning
The system SHALL support versioning of change artifacts as they evolve through iterations.

#### Scenario: Track artifact evolution
- **WHEN** a design document is updated during the Plan phase inner loop
- **THEN** each version SHALL be committed to Git
- **AND** the history SHALL be available for review

### Requirement: Artifact access API
The system SHALL provide an API for reading and writing change artifacts.

#### Scenario: Read artifact
- **WHEN** a component needs to read a design document
- **THEN** the system SHALL provide the artifact content from the change directory

#### Scenario: Write artifact
- **WHEN** an Agent generates a new artifact
- **THEN** the system SHALL write the artifact to the appropriate location
- **AND** handle the Git commit if configured

### Requirement: Archive completed changes
The system SHALL support archiving completed changes to reduce clutter.

#### Scenario: Archive change
- **GIVEN** a change is in the "Done" stage
- **WHEN** the user initiates archive
- **THEN** the system SHALL move the change directory to `.mohist/changes/archive/`
