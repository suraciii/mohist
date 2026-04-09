## ADDED Requirements

### Requirement: Workflow stages definition

The system SHALL support the following workflow stages: Explore, Plan, Build, Review, and Done.

#### Scenario: Stage mapping from old to new
- **GIVEN** the old workflow has stages: Draft, Plan, Build, Check, Review, Done
- **WHEN** migrating to the new workflow
- **THEN** the system SHALL map:
  - Draft → Explore
  - Plan → Plan (Planner Agent)
  - Build → Build (Coder Agent via spawn_coder)
  - Check → Review (Reviewer Agent)
  - Review → Review (Reviewer Agent)
  - Done → Done

#### Scenario: Issue starts in Explore stage
- **WHEN** a user creates a new issue
- **THEN** the issue SHALL be in the "Explore" stage

#### Scenario: Transition from Explore to Plan
- **WHEN** a user confirms to start planning from the Explore stage
- **THEN** the issue SHALL transition to the "Plan" stage

#### Scenario: Transition from Plan to Build
- **WHEN** the Planner Agent completes design and user approves
- **THEN** the issue SHALL transition to the "Build" stage

#### Scenario: Transition from Build to Review
- **WHEN** all tasks in the Build phase complete
- **THEN** the issue SHALL transition to the "Review" stage

#### Scenario: Transition from Review to Done
- **WHEN** the Reviewer Agent approves and user approves
- **THEN** the issue SHALL transition to the "Done" stage

### Requirement: Stage transition validation

The system SHALL validate that stage transitions follow the allowed workflow path.

Allowed transitions:
- Explore → Plan
- Plan → Build (after user approval)
- Build → Review
- Review → Done (after user approval)
- Review → Build (if user requests changes)
- Any stage → Paused (user-initiated)
- Paused → Current stage (resume)

#### Scenario: Invalid stage transition rejected
- **WHEN** an attempt is made to transition from "Build" directly to "Explore"
- **THEN** the system SHALL reject the transition with an error message

### Requirement: Workflow execution coordination

The system SHALL coordinate the execution of workflow stages and trigger appropriate agents.

#### Scenario: Execute Plan phase
- **GIVEN** an issue is in the "Plan" stage
- **WHEN** the workflow controller executes the stage
- **THEN** the system SHALL:
  1. Invoke the Planner Agent with the issue context
  2. Planner Agent generates design artifacts and self-reviews
  3. Planner Agent can self-correct if issues found
  4. Present design to user for approval

#### Scenario: Execute Build phase
- **GIVEN** an issue is in the "Build" stage
- **WHEN** the workflow controller executes the stage
- **THEN** the system SHALL:
  1. Read tasks from prd.json
  2. Sequentially execute each task using Coder Agent (spawn_coder)
  3. For each task failure, retry up to 3 times
  4. If still failing, pause and notify user
  5. When all tasks complete, transition to Review

#### Scenario: Execute Review phase
- **GIVEN** an issue is in the "Review" stage
- **WHEN** the workflow controller executes the stage
- **THEN** the system SHALL:
  1. Invoke the Reviewer Agent with the code context
  2. Reviewer Agent reviews code according to Prompt-defined dimensions
  3. If review passes, present to user for approval
  4. If review fails, suggest fixes or ask user for decision

### Requirement: Agent iteration control

Agents SHALL handle their own iteration logic internally, without external LoopController.

#### Scenario: Planner Agent self-correction
- **GIVEN** the Planner Agent is generating a design
- **WHEN** self-review finds issues
- **THEN** the Planner Agent SHALL:
  1. Fix the identified issues
  2. Re-review the fixed design
  3. Repeat until satisfied or max internal iterations reached
  4. Present final design to user

#### Scenario: Reviewer Agent review cycle
- **GIVEN** the Reviewer Agent is reviewing code
- **WHEN** issues are found
- **THEN** the Reviewer Agent SHALL:
  1. Provide detailed feedback
  2. Suggest specific fixes
  3. Present to user for decision (approve/request changes/abort)

### Requirement: User approval interface

The system SHALL provide a user approval mechanism at Plan and Review phases.

#### Scenario: Plan phase user approval
- **GIVEN** the Planner Agent has generated a design
- **WHEN** the system presents the design for approval
- **THEN** it SHALL display:
  - Proposal summary
  - Design document overview
  - Any self-review notes
- **AND** it SHALL allow user to: approve, request changes, or abort

#### Scenario: Review phase user approval
- **GIVEN** the Reviewer Agent has reviewed the code
- **WHEN** the system presents the review for approval
- **THEN** it SHALL display:
  - Code changes summary
  - Review results
  - Any issues found
- **AND** it SHALL allow user to: approve, request changes, or abort

## UPDATED Specifications

### WorkflowController Interface

```typescript
class WorkflowController {
  constructor(options: {
    plannerAgent: PlannerAgent;
    reviewerAgent: ReviewerAgent;
    artifactManager: ChangeArtifactsManager;
  });

  async executeStage(
    issue: Issue,
    stage: Stage
  ): Promise<StageResult>;

  validateTransition(
    from: Stage,
    to: Stage
  ): boolean;
}

type StageResult = {
  success: boolean;
  requiresApproval: boolean;
  output: unknown;
  message?: string;
};
```

### Agent Interface (Simplified)

```typescript
interface PlannerAgent {
  plan(options: {
    issue: Issue;
    worktreePath: string;
    prompt?: string; // Custom prompt template
  }): Promise<PlanResult>;
}

interface ReviewerAgent {
  review(options: {
    issue: Issue;
    worktreePath: string;
    prompt?: string; // Custom prompt template
  }): Promise<ReviewResult>;
}

type PlanResult = {
  success: boolean;
  artifacts: {
    proposal: string;
    design: string;
    specs: Array<{ name: string; content: string }>;
    prd: PrdJson;
  };
  selfReviewNotes?: string;
  iterations: number;
};

type ReviewResult = {
  passed: boolean;
  dimensions: Array<{
    name: string;
    passed: boolean;
    reasoning: string;
    issues?: Array<{
      severity: 'error' | 'warning';
      location: string;
      message: string;
    }>;
  }>;
  overallReasoning: string;
  fixSuggestions?: string[];
};
```
