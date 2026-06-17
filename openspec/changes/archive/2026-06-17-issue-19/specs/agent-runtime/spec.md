## ADDED Requirements

### Requirement: Agent runtime settings expose effective scheduling configuration
Agent runtime SHALL expose the effective runtime scheduling configuration needed by settings clients. The exposed configuration SHALL include maximum concurrent agents, session timeout, task timeout, stage timeout, and maximum grace periods when those values are available from supported configuration.

#### Scenario: Runtime configuration is available
- **WHEN** a settings client requests agent runtime settings
- **AND** supported configuration contains runtime scheduling values
- **THEN** the response SHALL include effective values for maximum concurrent agents, session timeout, task timeout, stage timeout, and maximum grace periods
- **AND** the values SHALL match the configuration the runtime uses for scheduling decisions

#### Scenario: Runtime configuration is partially unavailable
- **WHEN** a settings client requests agent runtime settings
- **AND** some runtime fields are not available from supported configuration
- **THEN** available fields SHALL still be returned
- **AND** unavailable fields SHALL be identified as unsupported or unavailable rather than failing the entire runtime settings contract

### Requirement: Agent runtime settings persist supported updates
Agent runtime SHALL persist updates for supported runtime scheduling settings through the supported configuration contract. Unsupported runtime fields MUST NOT be accepted as successfully saved.

#### Scenario: Supported runtime setting is updated
- **WHEN** a settings client updates `maxConcurrentAgents`, `agentTimeout`, `taskTimeout`, `stageTimeout`, or `maxGracePeriods`
- **THEN** the update SHALL be persisted through supported configuration
- **AND** subsequent runtime settings reads SHALL return the updated value

#### Scenario: Unsupported runtime setting is submitted
- **WHEN** a settings client submits a runtime field that cannot be persisted by the supported backend contract
- **THEN** the update SHALL be rejected or reported as unsupported
- **AND** the runtime settings state SHALL NOT present the field as saved

### Requirement: Agent runtime settings support reset only for persistable fields
Agent runtime SHALL provide reset behavior only for runtime settings whose default or configured value can be restored through supported configuration. Reset MUST NOT be exposed as successful for unsupported fields.

#### Scenario: Supported runtime setting is reset
- **WHEN** a settings client resets a supported runtime scheduling setting
- **THEN** the setting SHALL return to its configured default or effective default value
- **AND** subsequent runtime settings reads SHALL show the reset value

#### Scenario: Unsupported runtime setting cannot be reset
- **WHEN** a runtime setting has no supported reset contract
- **THEN** that setting SHALL be marked as unsupported for reset
- **AND** reset actions SHALL NOT report success for that setting
