## MODIFIED Requirements

### Requirement: Issue body storage remains plain text after CLI body ingestion

The local issue store SHALL persist the resolved issue body text exactly as received from the CLI/API write path, without storing CLI-specific body source syntax such as `@file`, `--body-file`, or `-`.

#### Scenario: Persist body loaded from file
- **WHEN** the CLI resolves `--body @body.md` before creating or updating an issue
- **THEN** the local issue store persists the file contents as plain issue body text
- **AND** does not persist the source token `@body.md`

#### Scenario: Persist body loaded from stdin
- **WHEN** the CLI resolves `--body -` before creating or updating an issue
- **THEN** the local issue store persists the streamed text as plain issue body text
- **AND** does not persist any stdin marker
