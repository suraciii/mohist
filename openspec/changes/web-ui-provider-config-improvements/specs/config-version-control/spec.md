## ADDED Requirements

### Requirement: Configuration versioning
The system SHALL support versioning of configuration files to enable conflict detection.

#### Scenario: Config has version field
- **WHEN** a configuration is saved with writeConfig()
- **THEN** the system SHALL add or update a `_version` field with current timestamp

#### Scenario: Config retains existing version on read
- **GIVEN** a config file with existing `_version` field
- **WHEN** the configuration is loaded with load()
- **THEN** the system SHALL preserve the `_version` value

#### Scenario: Config without version is backwards compatible
- **GIVEN** a config file without `_version` field (legacy)
- **WHEN** the configuration is loaded with load()
- **THEN** the system SHALL treat it as valid and assign a default version

### Requirement: Optimistic locking for config writes
The system SHALL support optimistic locking to prevent configuration overwrites.

#### Scenario: Write succeeds with matching version
- **GIVEN** a configuration with version 1000
- **WHEN** writeConfig() is called with expectedVersion: 1000
- **THEN** the write SHALL succeed and update the version

#### Scenario: Write fails with mismatched version
- **GIVEN** a configuration with version 1000 has been modified by another process to version 1001
- **WHEN** writeConfig() is called with expectedVersion: 1000
- **THEN** the system SHALL throw ConfigConflictError

#### Scenario: Write without version check succeeds
- **GIVEN** any configuration state
- **WHEN** writeConfig() is called without expectedVersion option
- **THEN** the write SHALL succeed (backward compatible behavior)

### Requirement: API version conflict handling
The system SHALL return appropriate HTTP status codes for version conflicts.

#### Scenario: API returns 409 on version conflict
- **GIVEN** a client sends POST /api/providers/:id with expectedVersion
- **WHEN** the configuration has been modified since that version
- **THEN** the system SHALL return 409 Conflict with error message

#### Scenario: API includes current version in conflict response
- **GIVEN** a version conflict occurs
- **WHEN** the API returns 409 Conflict
- **THEN** the response body SHALL include the current configuration version

#### Scenario: Successful write returns new version
- **GIVEN** a write operation succeeds
- **WHEN** the API returns success response
- **THEN** the response body SHALL include the new configuration version
