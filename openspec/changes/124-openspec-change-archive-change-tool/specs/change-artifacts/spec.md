## MODIFIED Requirements

### Requirement: Change archival
The system SHALL archive completed Changes to `openspec/changes/archive/YYYY-MM-DD-{name}/` with date prefix and conflict handling. Archival SHALL NOT sync delta specs to `openspec/specs/`. The `openspec/archive/` directory SHALL NOT be used.

#### Scenario: Archive completed change
- **WHEN** a Change is archived
- **THEN** the system moves the directory from `openspec/changes/{name}/` to `openspec/changes/archive/YYYY-MM-DD-{name}/`
- **AND** `YYYY-MM-DD` is the current date in local timezone
- **AND** the session-memories are preserved in the archive
- **AND** the Change remains accessible for historical reference
- **AND** the system SHALL NOT sync or copy any spec files to `openspec/specs/`

#### Scenario: Archive directory naming conflict
- **WHEN** a Change named `42-fix-auth` is archived on 2026-05-01
- **AND** `openspec/changes/archive/2026-05-01-42-fix-auth/` already exists
- **THEN** the system creates `openspec/changes/archive/2026-05-01-42-fix-auth-v2/`
- **AND** subsequent conflicts increment to `-v3`, `-v4`, etc.

#### Scenario: No spec sync during archive
- **WHEN** a Change is archived
- **THEN** the system SHALL NOT modify any files under `openspec/specs/`
- **AND** only the move operation from `changes/` to `changes/archive/` occurs
