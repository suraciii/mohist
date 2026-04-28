## ADDED Requirements

### Requirement: Task dependency graph validation on load
The system SHALL validate the task dependency graph when loading tasks.json, before any task execution begins.

**Validation checks:**
1. Every `dependsOn` entry MUST reference a task ID that exists in the task list
2. The dependency graph MUST be a DAG (no cycles)
3. Every `dependsOn` entry MUST reference a task with a lower `order` value (no forward dependencies)

#### Scenario: Valid dependency graph passes validation
- **WHEN** tasks.json is loaded with tasks T-001, T-002 (dependsOn: ["T-001"]), T-003 (dependsOn: ["T-002"])
- **THEN** validation passes with no errors
- **AND** task execution proceeds normally

#### Scenario: Unknown task ID in dependsOn fails validation
- **WHEN** tasks.json is loaded with a task whose `dependsOn` contains "T-999" which does not exist
- **THEN** validation SHALL fail
- **AND** the system SHALL log a descriptive error identifying the invalid reference
- **AND** the system SHALL treat this as a non-retryable failure

#### Scenario: Circular dependency detected
- **WHEN** tasks.json is loaded with T-001 (dependsOn: ["T-002"]) and T-002 (dependsOn: ["T-001"])
- **THEN** validation SHALL fail
- **AND** the system SHALL log a descriptive error identifying the cycle
- **AND** the system SHALL treat this as a non-retryable failure

#### Scenario: Forward dependency detected
- **WHEN** tasks.json is loaded with T-001 (order: 1, dependsOn: ["T-002"]) and T-002 (order: 2)
- **THEN** validation SHALL fail because T-001 depends on a higher-order task
- **AND** the system SHALL log a descriptive error

#### Scenario: Empty or missing dependsOn is valid
- **WHEN** a task has `dependsOn: []` or no `dependsOn` field
- **THEN** validation SHALL pass for that task
- **AND** the task is considered to have no dependencies (can execute immediately)
