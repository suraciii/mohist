## ADDED Requirements

### Requirement: Settings System log level reflects persisted state
Settings System diagnostics SHALL expose the current persisted log level as the source of truth for the Log Level control. The supported log levels SHALL be `DEBUG`, `INFO`, `WARN`, and `ERROR`.

#### Scenario: Current log level is displayed
- **WHEN** a user opens Settings > System
- **AND** the persisted log level is `WARN`
- **THEN** the Log Level control SHALL display `WARN`
- **AND** it SHALL NOT default to `INFO` unless `INFO` is the persisted value

#### Scenario: Supported levels are available
- **WHEN** the Log Level control is rendered
- **THEN** the available choices SHALL be `DEBUG`, `INFO`, `WARN`, and `ERROR`
- **AND** unsupported values SHALL NOT be selectable

### Requirement: Settings System log level changes are durable and explicit
Settings System diagnostics SHALL persist log level changes through a supported backend contract and SHALL make save failures visible to the user. The UI MUST NOT present a failed log-level change as successful.

#### Scenario: Log level update succeeds
- **WHEN** a user changes the log level to `ERROR`
- **AND** the backend accepts the update
- **THEN** the persisted log level SHALL become `ERROR`
- **AND** subsequent Settings > System loads SHALL display `ERROR`

#### Scenario: Log level update fails
- **WHEN** a user changes the log level
- **AND** the backend rejects or fails the update
- **THEN** Settings > System SHALL show a visible error message
- **AND** the Log Level control SHALL return to or retain the last confirmed persisted value

### Requirement: Settings System diagnostics avoid misleading defaults
Settings System diagnostics SHALL render unavailable diagnostic data as unavailable instead of substituting misleading healthy defaults such as `unknown`, `Up to date`, or `Stopped`.

#### Scenario: Optional diagnostic field is unavailable
- **WHEN** Settings > System cannot load a diagnostic field from a supported backend response
- **THEN** the affected field SHALL render an explicit unavailable state
- **AND** it SHALL NOT imply a healthy, stopped, or up-to-date state without data
