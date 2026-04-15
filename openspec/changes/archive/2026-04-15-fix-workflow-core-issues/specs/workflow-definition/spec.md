## MODIFIED Requirements

### Requirement: Single Source of Truth for Stage Transitions

The system SHALL use `STAGE_TRANSITIONS` and `isValidTransition()` from `types/index.ts` as the sole definition of allowed stage transitions. The hardcoded `M1_ALLOWED_TRANSITIONS` constant in `advance-stage.ts` SHALL be removed.

#### Scenario: advance-stage uses unified rules
- **WHEN** the advance_stage tool validates a transition from stage A to stage B
- **THEN** it SHALL call isValidTransition(A, B) imported from types/index.ts

#### Scenario: all stage flows supported
- **WHEN** an issue uses new flow stages (Explore, Plan, Build, Review, Done)
- **THEN** transitions SHALL be valid: Explore→Plan, Plan→Build, Build→Review, Review→Done, Review→Build

#### Scenario: legacy stage flows supported
- **WHEN** an issue uses legacy stages (Draft, Check)
- **THEN** transitions SHALL be valid: Draft→Plan, Check→Done, Check→Plan

### Requirement: Workflow Configuration Covers All Stages

The DEFAULT_WORKFLOW in workflow-loader.ts SHALL include configuration entries for all stages used in both new and legacy flows, with correct approval flags.

#### Scenario: new flow stages configured
- **WHEN** loadWorkflow returns the DEFAULT_WORKFLOW
- **THEN** it SHALL contain entries for explore, plan, build, review, and done stages

#### Scenario: approval flags enable pause at correct points
- **WHEN** AgentRunnerService.shouldPauseAtCurrentStage() checks the workflow
- **THEN** stages preceding plan and review SHALL have approval: true on the next stage, causing pause before plan execution and after review completion
