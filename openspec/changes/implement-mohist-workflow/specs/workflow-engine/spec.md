## ADDED Requirements

### Requirement: Workflow stages definition
The system SHALL support the following workflow stages: Explore, Plan, Build, Review, and Done.

#### Scenario: Issue starts in Explore stage
- **WHEN** a user creates a new issue
- **THEN** the issue SHALL be in the "Explore" stage

#### Scenario: Transition from Explore to Plan
- **WHEN** a user confirms to start planning from the Explore stage
- **THEN** the issue SHALL transition to the "Plan" stage

#### Scenario: Transition from Plan to Build
- **WHEN** the Plan phase completes and user approves
- **THEN** the issue SHALL transition to the "Build" stage

#### Scenario: Transition from Build to Review
- **WHEN** all tasks in the Build phase complete
- **THEN** the issue SHALL transition to the "Review" stage

#### Scenario: Transition from Review to Done
- **WHEN** the Review phase completes and user approves
- **THEN** the issue SHALL transition to the "Done" stage

### Requirement: Stage transition validation
The system SHALL validate that stage transitions follow the allowed workflow path.

#### Scenario: Invalid stage transition rejected
- **WHEN** an attempt is made to transition from "Build" directly to "Explore"
- **THEN** the system SHALL reject the transition with an error message

### Requirement: Workflow execution coordination
The system SHALL coordinate the execution of workflow stages and trigger appropriate agents based on the current stage.

#### Scenario: Execute Plan phase
- **GIVEN** an issue is in the "Plan" stage
- **WHEN** the workflow controller executes the stage
- **THEN** the system SHALL invoke the Plan Agent to generate design artifacts
- **AND** invoke Review Agents to evaluate the artifacts

#### Scenario: Execute Build phase
- **GIVEN** an issue is in the "Build" stage
- **WHEN** the workflow controller executes the stage
- **THEN** the system SHALL sequentially execute tasks from prd.json
- **AND** invoke external Coder agents for each task
