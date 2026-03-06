## ADDED Requirements

### Requirement: Seven-stage Workflow

The system SHALL implement a 7-stage workflow for Issue processing.

#### Scenario: Stage sequence

- **WHEN** processing an Issue
- **THEN** system SHALL execute stages in order:
  1. Exploration
  2. Refinement
  3. Design
  4. Implementation
  5. Review
  6. Done
  7. Re-evaluation (optional, manual trigger)

#### Scenario: Stage transitions

- **WHEN** a stage completes successfully
- **THEN** system SHALL transition to the next stage
- **AND** system SHALL update the stage:* label on the Issue

### Requirement: Exploration Stage

The exploration stage SHALL gather initial understanding of the Issue.

#### Scenario: Analyze Issue requirements

- **WHEN** in exploration stage
- **THEN** sub-agent SHALL read and analyze the Issue description
- **AND** sub-agent SHALL explore the codebase for relevant context

#### Scenario: Generate initial understanding

- **WHEN** exploration completes
- **THEN** sub-agent SHALL produce an initial understanding document
- **AND** sub-agent SHALL add a comment to the Issue with findings

### Requirement: Refinement Stage

The refinement stage SHALL clarify and complete requirements.

#### Scenario: Identify missing information

- **WHEN** in refinement stage
- **THEN** sub-agent SHALL identify any missing or unclear requirements
- **AND** sub-agent SHALL ask clarifying questions if needed

#### Scenario: Complete requirements

- **WHEN** refinement completes
- **THEN** sub-agent SHALL update the Issue with complete requirements
- **AND** sub-agent SHALL add a comment confirming requirements are ready

### Requirement: Design Stage

The design stage SHALL produce design specifications.

#### Scenario: Generate specs (OpenSpec optional)

- **WHEN** in design stage
- **THEN** sub-agent SHALL attempt to use OpenSpec CLI if available
- **AND** sub-agent SHALL fallback to manual spec generation if OpenSpec unavailable
- **AND** specs SHALL be stored in either:
  - `openspec/changes/issue-{N}/` (if using OpenSpec)
  - `specs/issue-{N}.md` (if manual)

#### Scenario: Mark OpenSpec usage in PR

- **WHEN** creating a PR
- **THEN** PR body SHALL indicate whether OpenSpec was used
- **AND** if OpenSpec was NOT used, PR body SHALL include note: "**注意**: 未使用 OpenSpec 格式，specs 手动生成"

#### Scenario: Create Draft PR

- **WHEN** specs are ready
- **THEN** sub-agent SHALL create a Draft PR
- **AND** sub-agent SHALL add specs files to the PR

#### Scenario: Wait for design trigger

- **WHEN** design trigger condition is met
- **THEN** system SHALL proceed to design stage
- **AND** system SHALL NOT auto-proceed without confirmation

**Design Trigger Conditions** (OR - 满足任一即可):
1. User explicitly says "可以设计了" in Issue comments
2. Issue Body contains at least 2 executable checkbox tasks (`- [ ]`)

**Optional Auxiliary Checks**:
- Issue has been in Refinement stage for > 5 minutes
- No unanswered questions in Issue comments

### Requirement: Implementation Stage

The implementation stage SHALL implement the solution based on specs.

#### Scenario: Implement based on specs

- **WHEN** in implementation stage
- **THEN** sub-agent SHALL implement the solution following the specs
- **AND** sub-agent SHALL commit changes to the same PR

#### Scenario: Mark PR ready for review

- **WHEN** implementation completes
- **THEN** sub-agent SHALL convert PR from Draft to Open
- **AND** sub-agent SHALL add a comment to the Issue with PR link

### Requirement: Review Stage

The review stage SHALL perform automated and manual review.

#### Scenario: Automated review

- **WHEN** in review stage
- **THEN** sub-agent SHALL perform automated code review
- **AND** sub-agent SHALL check for common issues

#### Scenario: Wait for user review

- **WHEN** automated review passes
- **THEN** system SHALL wait for user to review the PR
- **AND** system SHALL NOT merge until user approval

#### Scenario: Address review comments

- **WHEN** user leaves review comments
- **THEN** sub-agent SHALL address the comments
- **AND** sub-agent SHALL commit fixes to the PR

### Requirement: Done Stage

The done stage SHALL finalize and merge the PR.

#### Scenario: Merge PR

- **WHEN** review is approved
- **THEN** sub-agent SHALL merge the PR
- **AND** sub-agent SHALL close the Issue

#### Scenario: Add final comment

- **WHEN** PR is merged
- **THEN** sub-agent SHALL add a final comment to the Issue
- **AND** sub-agent SHALL include PR link and summary

### Requirement: Re-evaluation Stage

The re-evaluation stage SHALL allow revisiting completed work.

#### Scenario: Manual trigger only

- **WHEN** user manually triggers re-evaluation
- **THEN** system SHALL re-evaluate the completed work
- **AND** system SHALL NOT auto-trigger re-evaluation

#### Scenario: Assess quality debt

- **WHEN** in re-evaluation stage
- **THEN** sub-agent SHALL assess if quality debt has accumulated
- **AND** sub-agent SHALL propose improvements if needed
