## ADDED Requirements

### Requirement: Agent timeout configuration schema
The system SHALL support the following timeout-related configuration keys in `~/.mohist/config.jsonc` under the `agent` object:

| Key | Type | Default | Unit | Description |
|-----|------|---------|------|-------------|
| `agent.taskTimeout` | number | 600 | seconds | Maximum execution time for a single agent task |
| `agent.stageTimeout` | number | 3600 | seconds | Maximum total execution time for a workflow stage |
| `agent.maxGracePeriods` | number | 2 | count | Maximum number of grace period renewals per task |

#### Scenario: Config file with explicit timeout values
- **WHEN** `~/.mohist/config.jsonc` contains `{"agent": {"taskTimeout": 900, "stageTimeout": 7200, "maxGracePeriods": 3}}`
- **THEN** the system uses 900s task timeout, 7200s stage timeout, and 3 max grace periods

#### Scenario: Config file with partial values
- **WHEN** `~/.mohist/config.jsonc` contains `{"agent": {"taskTimeout": 1200}}`
- **THEN** the system uses 1200s task timeout, 3600s stage timeout (default), and 2 max grace periods (default)

#### Scenario: Config file missing agent section
- **WHEN** `~/.mohist/config.jsonc` exists but has no `agent` key
- **THEN** the system uses all default values (taskTimeout=600, stageTimeout=3600, maxGracePeriods=2)

#### Scenario: No config file exists
- **WHEN** `~/.mohist/config.jsonc` does not exist
- **THEN** the system uses all default values

### Requirement: Timeout value validation
The system SHALL validate timeout configuration values and reject invalid inputs.

**Validation Rules:**
- `taskTimeout`: MUST be >= 60 and <= 7200 (1 minute to 2 hours)
- `stageTimeout`: MUST be >= 300 and <= 86400 (5 minutes to 24 hours)
- `maxGracePeriods`: MUST be >= 0 and <= 10

#### Scenario: Negative timeout value rejected
- **WHEN** user sets `agent.taskTimeout` to -100
- **THEN** the system rejects the value with error: "taskTimeout must be between 60 and 7200 seconds"

#### Scenario: Excessively large timeout rejected
- **WHEN** user sets `agent.stageTimeout` to 200000
- **THEN** the system rejects the value with error: "stageTimeout must be between 300 and 86400 seconds"

#### Scenario: Non-numeric value rejected
- **WHEN** user sets `agent.taskTimeout` to "fast"
- **THEN** the system rejects the value with error: "taskTimeout must be a number"

### Requirement: Config change takes effect without restart
The system SHALL apply timeout configuration changes on the next issue start without requiring a server restart.

#### Scenario: Update config between issues
- **WHEN** user updates `agent.taskTimeout` via API while no issue is running
- **AND** user starts a new issue
- **THEN** the new issue uses the updated taskTimeout value

#### Scenario: Running issue unaffected by config change
- **WHEN** user updates `agent.taskTimeout` while an issue is in the build stage
- **THEN** the currently running issue continues with its original timeout value
- **AND** the next issue started will use the updated value

### Requirement: Timeout config API exposure
The system SHALL expose timeout configuration through the existing config API endpoints.

#### Scenario: Read timeout config via API
- **WHEN** client calls `GET /api/config`
- **THEN** the response includes `agent.taskTimeout`, `agent.stageTimeout`, and `agent.maxGracePeriods` with current values

#### Scenario: Update timeout config via API
- **WHEN** client calls `PUT /api/config/agent.taskTimeout` with `{"value": 900}`
- **THEN** the system validates and stores the value in config.jsonc
- **AND** subsequent `GET /api/config` returns the updated value
