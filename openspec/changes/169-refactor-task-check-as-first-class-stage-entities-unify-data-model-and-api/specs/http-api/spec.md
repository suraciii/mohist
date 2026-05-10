## ADDED Requirements

### Requirement: REQ-HTTP-001 Issue stage-state API exposes current progress

The HTTP API SHALL expose a unified issue stage-state endpoint for current task, check, and approval progress. The endpoint SHALL return normalized status values and SHALL be the primary API used by Issue Detail for task/check progress.

#### Scenario: Stage-state endpoint returns normalized current state

- **WHEN** a client requests `GET /api/issues/:number/stage-state`
- **THEN** the response SHALL include stage states for the issue
- **AND** each stage state SHALL include tasks, checks, approval, stage status, and updated timestamp fields
- **AND** task statuses SHALL use a normalized task status vocabulary
- **AND** check statuses SHALL use a normalized check status vocabulary

#### Scenario: Stage-state endpoint handles retried stages

- **WHEN** an issue has multiple execution attempts for the same stage
- **THEN** `GET /api/issues/:number/stage-state` SHALL return current latest task/check state
- **AND** it SHALL NOT expose the first execution attempt as active current progress

#### Scenario: Legacy progress data is projected when needed

- **WHEN** current stage-state rows are missing for an existing active issue
- **THEN** the endpoint SHALL return the best available current state by using backend projection or lazy seeding
- **AND** it SHALL avoid returning contradictory empty state when legacy task/check evidence exists

### Requirement: REQ-HTTP-002 Execution history remains separate

Execution history APIs SHALL remain separate from current stage-state APIs. `GET /api/issues/:number/executions` MAY continue to expose stage attempts for audit/debug consumers, but SHALL NOT be required for Issue Detail primary current progress rendering.

#### Scenario: Execution history remains available

- **WHEN** a client requests `GET /api/issues/:number/executions`
- **THEN** the response SHALL continue to return execution history rows
- **AND** preserving execution history SHALL NOT change the current-state endpoint semantics

#### Scenario: Compatibility endpoints do not define primary progress

- **WHEN** `/tasks` or `/build-status` remain available for compatibility
- **THEN** Issue Detail primary task/check rendering SHALL use the stage-state endpoint instead
- **AND** compatibility endpoints SHALL NOT be the source of contradictory UI progress
