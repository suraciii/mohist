## ADDED Requirements

### Requirement: Planner Agent design generation

The Planner Agent SHALL generate comprehensive design artifacts for an issue.

#### Scenario: Generate design from issue
- **GIVEN** an issue with title and description
- **WHEN** the Planner Agent executes
- **THEN** it SHALL:
  1. Explore the codebase to understand existing patterns
  2. Generate proposal.md with problem/solution overview
  3. Generate design.md with technical decisions
  4. Generate specs/ directory with capability specifications
  5. Generate prd.json with task breakdown

#### Scenario: Self-review design quality
- **GIVEN** design artifacts have been generated
- **WHEN** self-review is performed
- **THEN** the Planner Agent SHALL check:
  - Completeness: Are all requirements covered?
  - Consistency: Does it align with existing patterns?
  - Feasibility: Can it be implemented?
  - Risks: Are potential issues identified?

#### Scenario: Self-correction iteration
- **GIVEN** self-review finds issues
- **WHEN** the Planner Agent fixes them
- **THEN** it SHALL:
  1. Fix identified issues
  2. Re-review the fixed design
  3. Track iteration count
  4. Present final design when satisfied

### Requirement: Prompt customization

The Planner Agent SHALL support custom Prompt templates.

#### Scenario: Use default prompt
- **GIVEN** no custom prompt is provided
- **WHEN** the Planner Agent executes
- **THEN** it SHALL use the built-in default prompt template

#### Scenario: Use custom prompt
- **GIVEN** a custom prompt template is provided
- **WHEN** the Planner Agent executes
- **THEN** it SHALL use the custom prompt
- **AND** the custom prompt SHALL define:
  - Design methodology
  - Review criteria
  - Output format

#### Scenario: Prompt template format
- **GIVEN** a prompt template file
- **THEN** it SHALL be in YAML format:
  ```yaml
  role: planner
  name: Custom Planner
  description: Custom planning methodology
  
  steps:
    - Explore codebase
    - Analyze requirements
    - Generate design
    - Review according to custom criteria
  
  review_criteria:
    - completeness
    - consistency
    - feasibility
    - performance
    - security
  
  output_format:
    artifacts:
      - proposal.md
      - design.md
      - specs/
      - prd.json
  ```

### Requirement: Artifact generation

The Planner Agent SHALL generate all required artifacts in the correct structure.

#### Scenario: Create change directory
- **GIVEN** issue #42 titled "user auth"
- **WHEN** the Planner Agent starts
- **THEN** it SHALL create directory `.mohist/changes/42-user-auth/`

#### Scenario: Generate proposal.md
- **WHEN** generating artifacts
- **THEN** proposal.md SHALL include:
  - Problem statement
  - Proposed solution
  - Expected impact
  - Timeline estimate

#### Scenario: Generate design.md
- **WHEN** generating artifacts
- **THEN** design.md SHALL include:
  - Architecture overview
  - Key design decisions
  - Component interactions
  - Risks and mitigations

#### Scenario: Generate specs/
- **WHEN** generating artifacts
- **THEN** specs/ directory SHALL contain:
  - One spec file per capability
  - Each spec defines requirements using GIVEN/WHEN/THEN format

#### Scenario: Generate prd.json
- **WHEN** generating artifacts
- **THEN** prd.json SHALL contain:
  - Project name
  - Description
  - Array of tasks with id, title, description, acceptance criteria

## UPDATED Specifications

### Planner Agent Interface

```typescript
class PlannerAgent {
  constructor(options: {
    llmConfig: LlmConfig;
    artifactManager: ChangeArtifactsManager;
    defaultPrompt?: string;
  });

  async plan(options: {
    issue: Issue;
    worktreePath: string;
    customPrompt?: string;
  }): Promise<PlanResult>;

  private async exploreCodebase(worktreePath: string): Promise<CodebaseInfo>;
  private async generateArtifacts(issue: Issue, codebaseInfo: CodebaseInfo): Promise<Artifacts>;
  private async selfReview(artifacts: Artifacts): Promise<ReviewResult>;
  private async fixIssues(artifacts: Artifacts, issues: Issue[]): Promise<Artifacts>;
}

type PlanResult = {
  success: boolean;
  changePath: string;
  artifacts: {
    proposal: string;
    design: string;
    specs: Array<{ name: string; content: string }>;
    prd: PrdJson;
  };
  selfReviewNotes: string;
  iterations: number;
  duration: number;
};

type Artifacts = {
  proposal: string;
  design: string;
  specs: Map<string, string>;
  prd: PrdJson;
};
```

### Default Prompt Template

```yaml
role: planner
name: Mohist Planner

description: |
  You are a Planner Agent for Mohist workflow.
  Your job is to create comprehensive design artifacts for a software change.

steps:
  1_explore:
    action: Explore the codebase
    details: |
      - Read existing code to understand patterns
      - Identify relevant files and components
      - Understand the architecture

  2_analyze:
    action: Analyze requirements
    details: |
      - Read the issue title and description
      - Identify what needs to be built
      - Clarify ambiguities if needed

  3_design:
    action: Create design artifacts
    artifacts:
      proposal.md:
        sections:
          - Problem: What problem does this solve?
          - Solution: High-level approach
          - Impact: Expected outcomes
          - Timeline: Rough estimate
      
      design.md:
        sections:
          - Overview: Architecture summary
          - Decisions: Key technical choices
          - Components: Main parts and interactions
          - Risks: Potential issues
      
      specs/:
        format: |
          ## ADDED Requirements
          
          ### Requirement: {capability}
          
          #### Scenario: {scenario}
          - **GIVEN** {context}
          - **WHEN** {action}
          - **THEN** {expected outcome}
      
      prd.json:
        format: |
          {
            "project": "project-name",
            "description": "...",
            "tasks": [
              {
                "id": "T-001",
                "title": "...",
                "description": "...",
                "acceptanceCriteria": [...]
              }
            ]
          }

  4_review:
    action: Self-review the design
    criteria:
      completeness:
        check: Are all requirements covered?
        severity: error if missing
      
      consistency:
        check: Does it align with existing patterns?
        severity: warning if different
      
      feasibility:
        check: Can this be implemented?
        severity: error if impossible
      
      risks:
        check: Are risks identified?
        severity: warning if missing

  5_fix:
    action: Fix identified issues
    condition: If any review criteria failed
    max_iterations: 3

output_format:
  final_artifacts:
    - proposal.md
    - design.md
    - specs/*.md
    - prd.json
  
  review_summary: |
    Provide a brief summary of the self-review:
    - Number of issues found and fixed
    - Any remaining concerns
    - Confidence level (high/medium/low)
```
