## ADDED Requirements

### Requirement: Epic Persistence

Local storage SHALL persist Epic records and primary Epic issue membership without changing issue workflow persistence semantics.

#### Scenario: Store Epic records

- **WHEN** an Epic is created
- **THEN** local storage records title, description, priority, status, created timestamp, and updated timestamp

#### Scenario: Store primary membership

- **WHEN** an issue is linked to an Epic
- **THEN** local storage records the Epic id, issue id, and membership creation timestamp
- **AND** local storage enforces uniqueness of issue id across primary Epic memberships

#### Scenario: Preserve issue rows

- **WHEN** Epic membership is added, removed, marked done, or closed
- **THEN** existing issue workflow fields remain unchanged

### Requirement: Epic Read Queries

Local storage SHALL support efficient Epic list, detail, membership, and issue backlink queries.

#### Scenario: List Epics with linked issues

- **WHEN** the service lists Epics
- **THEN** storage can load each Epic and its linked issues for progress projection

#### Scenario: Read issue primary Epic

- **WHEN** the service reads an issue detail backlink
- **THEN** storage can return the primary Epic summary for that issue, if one exists
