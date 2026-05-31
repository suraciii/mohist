## ADDED Requirements

### Requirement: Task artifact expectations exclude verdict markers
Workflow task definitions SHALL describe artifact completion requirements as required files and optional neutral artifact markers or content inside those files. Task artifact expectations MUST NOT model PASS, FAIL, `<promise>PASS</promise>`, `<promise>FAIL</promise>`, or any other pass/fail verdict marker as a task completion requirement.

#### Scenario: Task requires only artifact files
- **WHEN** a workflow task definition declares artifact expectations
- **THEN** the definition SHALL express each required artifact as a required file path
- **AND** task completion SHALL NOT depend on a pass/fail verdict marker in that file

#### Scenario: Task marker is artifact shape content
- **WHEN** a workflow task definition declares an optional marker for an artifact file
- **THEN** the marker SHALL represent neutral artifact shape or required content
- **AND** the marker SHALL NOT represent PASS or FAIL semantics

#### Scenario: Verdict marker configured as task artifact marker
- **WHEN** a workflow task definition configures a PASS/FAIL-like verdict marker as a task artifact expectation
- **THEN** definition loading SHALL reject the task definition or produce a clear schema diagnostic
- **AND** the diagnostic SHALL tell profile authors to move verdict marker requirements into a check definition

#### Scenario: Built-in workflow profiles use artifact language for tasks
- **WHEN** the system loads built-in workflow profile definitions
- **THEN** task expectation fields and names SHALL use artifact-focused language
- **AND** no built-in task artifact expectation SHALL require a PASS or FAIL verdict marker

### Requirement: Check definitions own verdict marker contracts
Workflow check definitions SHALL be the only declarative workflow definitions that require pass/fail verdict markers. A check definition MAY require verdict evidence such as `PASS` or `<promise>PASS</promise>`, and that requirement SHALL be interpreted as check verdict validation rather than task artifact completion.

#### Scenario: Check requires pass verdict marker
- **WHEN** a workflow check definition declares a required verdict marker
- **THEN** the runner SHALL evaluate that marker as check verdict evidence
- **AND** the requirement SHALL NOT be copied into the producing task's artifact expectations

#### Scenario: Failed review verdict remains check evidence
- **WHEN** `ai-review` produces `review.md` containing `<promise>FAIL</promise>`
- **AND** `review-passed` requires `<promise>PASS</promise>`
- **THEN** the workflow definition SHALL model the failed PASS requirement as a `review-passed` check verdict failure
- **AND** it SHALL NOT model the missing PASS marker as an `ai-review` task artifact failure
