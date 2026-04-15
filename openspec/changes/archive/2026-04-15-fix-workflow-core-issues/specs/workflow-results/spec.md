## ADDED Requirements

### Requirement: Shared PlanResult Type

The system SHALL define a single `PlanResult` interface in `types/workflow-results.ts` that is the authoritative type for all plan stage results.

#### Scenario: Planner Agent returns PlanResult
- **WHEN** PlannerAgent.plan() completes
- **THEN** it SHALL return a value conforming to the shared PlanResult interface with fields: success, changePath, artifacts, iterations, duration, and optional selfReviewNotes, error

#### Scenario: WorkflowController consumes PlanResult
- **WHEN** WorkflowController.executePlanStage() processes the planner result
- **THEN** it SHALL import PlanResult from types/workflow-results.ts and use it as the return type

### Requirement: Shared ReviewResult Type

The system SHALL define a single `ReviewResult` interface in `types/workflow-results.ts` that is the authoritative type for all review stage results.

#### Scenario: Reviewer Agent returns ReviewResult
- **WHEN** ReviewerAgent.review() completes
- **THEN** it SHALL return a value conforming to the shared ReviewResult interface with fields: passed, dimensions, overallReasoning, duration, and optional fixSuggestions

#### Scenario: WorkflowController consumes ReviewResult
- **WHEN** WorkflowController.executeReviewStage() processes the reviewer result
- **THEN** it SHALL import ReviewResult from types/workflow-results.ts and use it as the return type

### Requirement: No Duplicate Type Definitions

The system SHALL NOT define PlanResult or ReviewResult interfaces in any file other than `types/workflow-results.ts`.

#### Scenario: Typecheck detects duplicate definitions
- **WHEN** the codebase is typechecked
- **THEN** no file other than types/workflow-results.ts SHALL export PlanResult or ReviewResult interfaces
