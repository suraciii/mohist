## ADDED Requirements

### Requirement: Reviewer Agent code review

The Reviewer Agent SHALL review code quality and provide structured feedback.

#### Scenario: Review code changes
- **GIVEN** code has been implemented in Build phase
- **WHEN** the Reviewer Agent executes
- **THEN** it SHALL:
  1. Identify all changed files
  2. Review each file according to Prompt-defined dimensions
  3. Provide structured feedback
  4. Suggest fixes if issues found

#### Scenario: Multi-dimensional review
- **WHEN** reviewing code
- **THEN** the Reviewer Agent SHALL evaluate:
  - Correctness: Logic errors, type safety, lint violations
  - Complexity: Function length, cyclomatic complexity, duplication
  - Test Coverage: Tests exist, tests pass, coverage adequate
  - Security: Common vulnerabilities, input validation

#### Scenario: Customizable review dimensions
- **GIVEN** a custom prompt defines different dimensions
- **WHEN** the Reviewer Agent executes
- **THEN** it SHALL review according to the custom dimensions
- **AND** the default dimensions MAY be overridden

### Requirement: Prompt customization

The Reviewer Agent SHALL support custom Prompt templates.

#### Scenario: Use default prompt
- **GIVEN** no custom prompt is provided
- **WHEN** the Reviewer Agent executes
- **THEN** it SHALL use the built-in default prompt template

#### Scenario: Use custom prompt
- **GIVEN** a custom prompt template is provided
- **WHEN** the Reviewer Agent executes
- **THEN** it SHALL use the custom prompt
- **AND** the custom prompt SHALL define review dimensions and criteria

#### Scenario: Prompt template format
- **GIVEN** a prompt template file
- **THEN** it SHALL be in YAML format:
  ```yaml
  role: reviewer
  name: Custom Reviewer
  description: Custom review methodology
  
  dimensions:
    correctness:
      checks:
        - Logic errors
        - Type safety
        - Lint violations
    
    performance:
      checks:
        - Algorithm complexity
        - Resource usage
        - Caching opportunities
    
    security:
      checks:
        - Input validation
        - Injection risks
        - Authentication/Authorization
  
  output_format:
    passed: boolean
    dimensions: array
    overall_reasoning: string
    fix_suggestions: array
  ```

### Requirement: Structured review output

The Reviewer Agent SHALL return structured output.

#### Scenario: Review passes
- **GIVEN** all review dimensions pass
- **WHEN** review completes
- **THEN** it SHALL return:
  ```json
  {
    "passed": true,
    "dimensions": [
      {
        "name": "correctness",
        "passed": true,
        "reasoning": "No logic errors found..."
      }
    ],
    "overallReasoning": "Code meets all quality standards..."
  }
  ```

#### Scenario: Review fails with issues
- **GIVEN** some review dimensions fail
- **WHEN** review completes
- **THEN** it SHALL return:
  ```json
  {
    "passed": false,
    "dimensions": [
      {
        "name": "correctness",
        "passed": false,
        "reasoning": "Logic error in function X...",
        "issues": [
          {
            "severity": "error",
            "location": "src/foo.ts:42",
            "message": "Null pointer dereference",
            "suggestion": "Add null check before accessing property"
          }
        ]
      }
    ],
    "overallReasoning": "Code has issues that need to be addressed...",
    "fixSuggestions": [
      "Fix null pointer in src/foo.ts",
      "Add input validation in src/bar.ts"
    ]
  }
  ```

### Requirement: Integration with test execution

The Reviewer Agent SHALL execute tests as part of review.

#### Scenario: Run tests during review
- **WHEN** reviewing code
- **THEN** the Reviewer Agent SHALL:
  1. Run the test suite
  2. Check test results
  3. Report test failures as correctness issues

#### Scenario: Check test coverage
- **WHEN** reviewing test coverage dimension
- **THEN** the Reviewer Agent SHALL:
  1. Check if new code has tests
  2. Run coverage report
  3. Report insufficient coverage

## UPDATED Specifications

### Reviewer Agent Interface

```typescript
class ReviewerAgent {
  constructor(options: {
    llmConfig: LlmConfig;
    defaultPrompt?: string;
  });

  async review(options: {
    issue: Issue;
    worktreePath: string;
    customPrompt?: string;
  }): Promise<ReviewResult>;

  private async getChangedFiles(worktreePath: string): Promise<string[]>;
  private async reviewFile(filePath: string, dimensions: Dimension[]): Promise<FileReview>;
  private async runTests(worktreePath: string): Promise<TestResult>;
  private async checkCoverage(worktreePath: string): Promise<CoverageResult>;
}

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
      suggestion?: string;
    }>;
  }>;
  overallReasoning: string;
  fixSuggestions?: string[];
  duration: number;
};

type Dimension = {
  name: string;
  checks: string[];
  weight?: number;
};
```

### Default Prompt Template

```yaml
role: reviewer
name: Mohist Reviewer

description: |
  You are a Reviewer Agent for Mohist workflow.
  Your job is to review code quality and provide structured feedback.

dimensions:
  correctness:
    description: Code correctness and quality
    checks:
      - name: Logic errors
        severity: error
        details: Check for bugs, off-by-one errors, edge cases
      
      - name: Type safety
        severity: error
        details: Verify TypeScript types are correct
      
      - name: Lint violations
        severity: warning
        details: Check against project linting rules
    
    execution: |
      Review the code for logic errors and type safety.
      Run lint checks and report violations.

  complexity:
    description: Code complexity metrics
    checks:
      - name: Function length
        severity: warning
        threshold: 50 lines
        details: Functions should be concise and focused
      
      - name: Cyclomatic complexity
        severity: warning
        threshold: 10
        details: Limit branching in single function
      
      - name: Code duplication
        severity: warning
        details: Check for copy-pasted code
    
    execution: |
      Analyze code for complexity issues.
      Suggest refactoring if needed.

  test_coverage:
    description: Test coverage and quality
    checks:
      - name: Tests exist
        severity: error
        details: New code must have tests
      
      - name: Tests pass
        severity: error
        details: All tests must pass
      
      - name: Coverage adequate
        severity: warning
        threshold: 80%
        details: Code coverage should be reasonable
    
    execution: |
      Run test suite and check results.
      Verify new code has tests.
      Check coverage reports.

  security:
    description: Security best practices
    checks:
      - name: Input validation
        severity: error
        details: Validate all external inputs
      
      - name: Injection risks
        severity: error
        details: Check for SQL, command, or code injection
      
      - name: Sensitive data
        severity: warning
        details: Ensure secrets are not exposed
    
    execution: |
      Review for common security vulnerabilities.
      Check input validation and sanitization.

review_process:
  1_identify:
    action: Identify changed files
    method: Git diff or file system scan

  2_review_each:
    action: Review each file
    for_each_dimension:
      - Check criteria
      - Record issues
      - Provide reasoning

  3_run_tests:
    action: Execute test suite
    on_failure:
      - Report as correctness issue
      - Include error details

  4_aggregate:
    action: Aggregate results
    rules:
      - Any error dimension → overall fail
      - All pass → overall pass
      - Warnings only → pass with warnings

  5_suggest:
    action: Suggest fixes
    format: |
      Provide specific, actionable fix suggestions:
      - File path
      - Line number (if applicable)
      - Suggested change

output_format:
  passed: boolean
  dimensions:
    - name: string
      passed: boolean
      reasoning: string
      issues:
        - severity: error | warning
          location: string
          message: string
          suggestion: string
  overall_reasoning: string
  fix_suggestions:
    - string
```
