## MODIFIED Requirements

### Requirement: REQ-STORE-001 Archived issue filtering preserves active and historical views

The local issue store SHALL preserve archived issue records while excluding them from default active issue queries. Archived issues SHALL remain retrievable through explicit archived queries.

#### Scenario: Default query hides archived issue
- **GIVEN** an issue has `archivedAt` set
- **WHEN** issues are queried without `includeArchived` or `archivedOnly`
- **THEN** the archived issue SHALL NOT be returned

#### Scenario: Archived-only query returns archived issue
- **GIVEN** an issue has `archivedAt` set
- **WHEN** issues are queried with `archivedOnly`
- **THEN** the archived issue SHALL be returned

#### Scenario: Archived issue keeps history fields
- **GIVEN** an issue has comments, logs, review results, or other historical metadata
- **WHEN** the issue is archived
- **THEN** the issue row SHALL remain available for historical views
- **AND** archive cleanup SHALL NOT delete the issue record
