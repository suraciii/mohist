## ADDED Requirements

### Requirement: Settings configuration API exposes log level
The HTTP API SHALL expose the current log level through a supported settings or configuration endpoint. The API SHALL accept only `DEBUG`, `INFO`, `WARN`, and `ERROR` as log-level values.

#### Scenario: Read current log level
- **WHEN** a client reads settings configuration
- **THEN** the response SHALL include the current persisted log level
- **AND** the value SHALL be one of `DEBUG`, `INFO`, `WARN`, or `ERROR`

#### Scenario: Update log level
- **WHEN** a client sends a supported log-level update with `WARN`
- **THEN** the API SHALL persist `WARN`
- **AND** a subsequent settings configuration read SHALL return `WARN`

#### Scenario: Reject invalid log level
- **WHEN** a client sends a log-level update with an unsupported value
- **THEN** the API SHALL return a 400-class validation error
- **AND** the previous log level SHALL remain unchanged

### Requirement: Settings configuration API exposes runtime scheduling settings
The HTTP API SHALL expose runtime scheduling settings through implemented endpoints only. The exposed settings SHALL include `maxConcurrentAgents`, `agentTimeout`, `taskTimeout`, `stageTimeout`, and `maxGracePeriods` when supported by configuration.

#### Scenario: Read runtime scheduling settings
- **WHEN** a client reads runtime settings through the supported API contract
- **THEN** the response SHALL include supported runtime scheduling values from configuration
- **AND** the API SHALL NOT require clients to call a missing endpoint to obtain equivalent values

#### Scenario: Update supported runtime scheduling setting
- **WHEN** a client updates `agentTimeout`, `maxConcurrentAgents`, `taskTimeout`, `stageTimeout`, or `maxGracePeriods` through the supported API contract
- **THEN** the API SHALL persist the new value
- **AND** a subsequent read SHALL return the updated value

#### Scenario: Unsupported runtime field is not silently accepted
- **WHEN** a client attempts to update a runtime field that the API cannot persist
- **THEN** the API SHALL return a 400-class or 404-class error indicating the field is unsupported
- **AND** the API SHALL NOT report a successful save

### Requirement: Settings API contract has regression coverage
The HTTP API SHALL have regression coverage for reading and updating log level and runtime scheduling settings, including successful updates and validation failures.

#### Scenario: Log level API behavior is tested
- **WHEN** backend settings API tests run
- **THEN** they SHALL verify reading the current log level, updating to a supported level, and rejecting an unsupported level

#### Scenario: Runtime configuration API behavior is tested
- **WHEN** backend settings API tests run
- **THEN** they SHALL verify reading runtime scheduling settings and updating each supported persistable runtime setting
