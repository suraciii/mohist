## ADDED Requirements

### Requirement: Settings System log level uses supported backend state
The Web UI SHALL render Settings > System Log Level from supported backend state and SHALL persist changes through a supported backend API. The Web UI MUST NOT silently ignore failed log-level updates.

#### Scenario: System log level loads real value
- **WHEN** a user opens Settings > System
- **AND** the backend reports `logLevel` as `DEBUG`
- **THEN** the Log Level control SHALL display `DEBUG`
- **AND** it SHALL NOT display a hardcoded `INFO` value

#### Scenario: System log level save succeeds
- **WHEN** a user changes Log Level to `ERROR`
- **AND** the backend save succeeds
- **THEN** the Web UI SHALL keep `ERROR` selected
- **AND** future loads SHALL show `ERROR` from backend state

#### Scenario: System log level save fails visibly
- **WHEN** a user changes Log Level
- **AND** the backend save fails
- **THEN** the Web UI SHALL show a visible error notification
- **AND** it SHALL NOT leave the user believing the failed value was saved

### Requirement: Settings Runtime loads from implemented backend contracts
The Web UI SHALL load Settings > Runtime from implemented backend contracts that provide equivalent runtime configuration values. The Runtime page MUST NOT fail solely because `/api/agent-runtime` is absent when equivalent values are available from supported configuration APIs.

#### Scenario: Runtime page loads on .NET server
- **WHEN** a user opens Settings > Runtime on the .NET server
- **AND** supported configuration APIs provide runtime scheduling values
- **THEN** the Runtime page SHALL render successfully
- **AND** it SHALL NOT show `Failed to load settings: Empty response from /agent-runtime`

#### Scenario: Runtime page shows effective values
- **WHEN** Runtime settings load succeeds
- **THEN** the page SHALL show effective values for concurrency, session timeout, task timeout, stage timeout, and grace-period behavior
- **AND** the values SHALL match the supported backend configuration response

#### Scenario: Runtime load failure is explicit
- **WHEN** all supported backend contracts needed for Runtime settings fail
- **THEN** the Runtime page SHALL show a visible load error
- **AND** it SHALL avoid rendering misleading default values as if they were persisted settings

### Requirement: Settings Runtime only enables supported edits
The Web UI SHALL enable save and reset actions only for runtime settings that the backend can persist or reset. Unsupported runtime fields SHALL be disabled with explanatory text.

#### Scenario: Supported runtime setting can be saved
- **WHEN** a user changes a supported runtime setting
- **AND** the backend save succeeds
- **THEN** the Web UI SHALL show the updated value as saved
- **AND** a refresh SHALL preserve the updated value

#### Scenario: Runtime save failure is visible
- **WHEN** a user saves a supported runtime setting
- **AND** the backend save fails
- **THEN** the Web UI SHALL show a visible error
- **AND** it SHALL NOT present the failed value as persisted

#### Scenario: Unsupported runtime field is disabled
- **WHEN** Runtime settings include a field that the backend cannot persist
- **THEN** the control for that field SHALL be disabled
- **AND** the page SHALL explain that the field is unavailable or unsupported

#### Scenario: Reset is available only for supported fields
- **WHEN** a runtime field has no supported reset contract
- **THEN** the Web UI SHALL disable or hide reset for that field
- **AND** it SHALL NOT report a reset success for that field

### Requirement: Settings pages avoid nonexistent endpoint dependencies
The Web UI SHALL avoid calling nonexistent settings endpoints when implemented endpoints provide the required data. Calls to missing settings endpoints MUST be removed or replaced unless those endpoints are implemented as supported contracts.

#### Scenario: Runtime does not depend on missing endpoint
- **WHEN** Settings > Runtime loads on the .NET server
- **THEN** the Web UI SHALL use implemented backend contracts for runtime settings
- **AND** it SHALL NOT require a successful call to a nonexistent `/api/agent-runtime` endpoint

#### Scenario: System log level does not depend on missing endpoint
- **WHEN** Settings > System loads or saves log level on the .NET server
- **THEN** the Web UI SHALL use an implemented backend contract for log-level state
- **AND** it SHALL NOT silently fail because `/api/log-level` is absent

### Requirement: Settings regression coverage protects restored behavior
The Web UI SHALL include regression coverage for Settings > System log level and Settings > Runtime load, unsupported-field, save-success, and save-failure states.

#### Scenario: System log level tests cover success and failure
- **WHEN** Web settings tests run
- **THEN** they SHALL verify real log-level loading, successful save behavior, and visible failed-save behavior

#### Scenario: Runtime tests cover load and edit states
- **WHEN** Web settings tests run
- **THEN** they SHALL verify Runtime load success, Runtime load failure, disabled unsupported fields, save success, and save failure behavior
