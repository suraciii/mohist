## ADDED Requirements

### Requirement: Inner loop execution for Plan phase
The system SHALL support an inner loop for the Plan phase where review failures trigger automatic fixes until success or max iterations.

#### Scenario: Plan phase succeeds on first attempt
- **GIVEN** the Plan phase generates design artifacts
- **WHEN** the review passes on the first iteration
- **THEN** the system SHALL proceed to user approval

#### Scenario: Plan phase requires one fix iteration
- **GIVEN** the Plan phase generates design artifacts
- **WHEN** the review fails on the first iteration
- **THEN** the system SHALL trigger the Fix Agent
- **AND** re-run the review after the fix
- **AND** proceed to user approval if the review passes

#### Scenario: Plan phase reaches max iterations
- **GIVEN** the Plan phase has failed review 5 times
- **WHEN** the 5th review iteration completes
- **THEN** the system SHALL pause the workflow
- **AND** notify the user with a detailed failure report

### Requirement: Inner loop execution for Review phase
The system SHALL support an inner loop for the Review phase where code review failures trigger automatic fixes until success or max iterations.

#### Scenario: Code review succeeds on first attempt
- **GIVEN** the Build phase has completed
- **WHEN** the code review passes on the first iteration
- **THEN** the system SHALL proceed to user approval

#### Scenario: Code review requires fixes
- **GIVEN** the code review identifies issues
- **WHEN** the review fails
- **THEN** the system SHALL trigger the Fix Agent to address the issues
- **AND** re-run the review after fixes
- **AND** repeat until passed or max iterations reached

### Requirement: Maximum iteration limits
The system SHALL enforce maximum iteration limits to prevent infinite loops.

#### Scenario: Plan phase iteration limit
- **GIVEN** the Plan phase inner loop is executing
- **WHEN** the iteration count reaches 5
- **THEN** the system SHALL stop the loop
- **AND** escalate to user intervention

#### Scenario: Review phase iteration limit
- **GIVEN** the Review phase inner loop is executing
- **WHEN** the iteration count reaches 3
- **THEN** the system SHALL stop the loop
- **AND** escalate to user intervention

### Requirement: Loop state management
The system SHALL maintain the state of inner loops including iteration count, history, and current status.

#### Scenario: Track loop iteration history
- **WHEN** an inner loop executes
- **THEN** the system SHALL record:
  - Iteration number
  - Execute output
  - Review results
  - Fix output (if applicable)
- **AND** make this history available for debugging

### Requirement: Escalation to user
When inner loops reach their maximum iterations without success, the system SHALL escalate to the user with sufficient context for decision-making.

#### Scenario: Plan phase escalation
- **GIVEN** the Plan phase has reached max iterations
- **WHEN** the system escalates to the user
- **THEN** the system SHALL provide:
  - Summary of all attempted iterations
  - Key issues that couldn't be resolved
  - Options: retry, modify requirements, or abort
