## ADDED Requirements

### Requirement: Runner slots configuration endpoint

The HTTP API SHALL expose a `PATCH` request on the runner resource (`/api/runner/{runnerId}`) that updates a runner's persisted `slots`. The request body SHALL carry the new `slots` value. A successful update SHALL persist the value to the runner definition state, the response SHALL reflect the updated runner definition state, and the updated `slots` SHALL take effect on the next dispatch cycle. A `slots` value that is not a positive integer SHALL be rejected and the persisted `slots` SHALL remain unchanged.

#### Scenario: PATCH updates persisted slots
- **WHEN** a client sends `PATCH /api/runner/{runnerId}` with a valid positive-integer `slots` value
- **THEN** the API SHALL persist the new `slots` to the runner definition state
- **AND** the response SHALL reflect the updated `slots`
- **AND** the next dispatch cycle SHALL honor the new capacity

#### Scenario: PATCH rejects non-positive slots
- **WHEN** a client sends a `slots` value that is not a positive integer
- **THEN** the API SHALL reject the request
- **AND** the persisted `slots` SHALL remain unchanged

### Requirement: Runner register and heartbeat concurrency field is non-authoritative for dispatch

The runner register and heartbeat endpoints MAY accept a `MaxWorkflowSlots` field for backward compatibility, but that field SHALL NOT be used as the source of dispatch capacity. Dispatch capacity SHALL be sourced exclusively from the persisted runner definition state. The field SHALL be retained only as runner-process local cognition.

#### Scenario: Reported MaxWorkflowSlots does not govern dispatch
- **WHEN** a runner registers or heartbeats carrying a `MaxWorkflowSlots` value
- **THEN** the API and grains SHALL NOT use that value to determine dispatch capacity
- **AND** the persisted `slots` SHALL remain the sole dispatch capacity source
