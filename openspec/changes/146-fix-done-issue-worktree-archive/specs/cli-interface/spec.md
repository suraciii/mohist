## MODIFIED Requirements

### Requirement: REQ-CLI-001 CLI archive output reflects backend archive semantics

The CLI SHALL remain a thin client for archive operations while making backend warning, skipped, and cleanup semantics clear to users.

#### Scenario: Single archive warning is not duplicated
- **GIVEN** the archive API returns a warning string
- **WHEN** the user runs `mo issue archive <number>`
- **THEN** the CLI SHALL display the warning once
- **AND** the output SHALL NOT contain `Warning: Warning:`

#### Scenario: Single archive no-cleanup forwards cleanup false
- **WHEN** the user runs `mo issue archive <number> --no-cleanup`
- **THEN** the CLI SHALL request archive with cleanup disabled

#### Scenario: Batch archive reports skipped issues
- **WHEN** the user runs `mo issue archive --all-completed`
- **THEN** the CLI SHALL show the number of archived issues
- **AND** the CLI SHALL show skipped issue count and skipped issue numbers when present
- **AND** the CLI SHALL explain skipped issues were not confirmed merged

#### Scenario: Batch no-cleanup semantics are explicit
- **WHEN** the user combines `--all-completed` with `--no-cleanup`
- **THEN** the CLI SHALL either forward cleanup disabled to the API or display a clear error that the combination is unsupported
- **AND** the CLI SHALL NOT silently ignore `--no-cleanup`
