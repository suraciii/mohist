## ADDED Requirements

### Requirement: Task artifact validation reports artifact marker failures
The workflow engine SHALL validate task artifact expectations as file existence and optional neutral artifact marker or content requirements. Missing neutral artifact markers MUST produce artifact-specific diagnostics that identify the artifact file and marker without describing the failure as a check verdict failure.

#### Scenario: Required task artifact file is missing
- **WHEN** a task completes without producing a required artifact file
- **THEN** task artifact validation SHALL fail the task
- **AND** the diagnostic SHALL identify the missing artifact file requirement

#### Scenario: Neutral task artifact marker is missing
- **WHEN** a task produces a required artifact file
- **AND** a neutral artifact marker declared for that file is missing
- **THEN** task artifact validation SHALL fail the task
- **AND** the diagnostic SHALL identify the missing artifact marker as an artifact requirement

#### Scenario: Task artifact validation ignores pass fail verdict semantics
- **WHEN** a task produces an artifact containing a parseable PASS or FAIL verdict marker
- **THEN** task artifact validation SHALL treat the file as present for artifact completion purposes
- **AND** it SHALL NOT decide task success from whether the verdict is PASS or FAIL

### Requirement: Check verdict validation reports verdict marker failures
The workflow engine SHALL validate required PASS/FAIL verdict markers only while executing checks. Missing, mismatched, or failing verdict markers MUST produce check-verdict diagnostics that identify the check and expected verdict marker rather than reporting a task artifact marker failure.

#### Scenario: Required pass verdict marker is missing
- **WHEN** a check requires a pass verdict marker from an existing artifact
- **AND** the artifact does not contain the required pass marker
- **THEN** check verdict validation SHALL fail the check
- **AND** the diagnostic SHALL identify the missing or mismatched verdict marker as check evidence

#### Scenario: Fail verdict does not become missing artifact marker
- **WHEN** `review.md` exists and contains `<promise>FAIL</promise>`
- **AND** `review-passed` requires `<promise>PASS</promise>`
- **THEN** `review-passed` SHALL fail as a check verdict failure
- **AND** the runner error message SHALL NOT report `review.md` as missing an artifact marker for the `ai-review` task

#### Scenario: Artifact marker and verdict marker tests remain separate
- **WHEN** automated tests exercise workflow validation
- **THEN** task file requirements, optional neutral task artifact markers, and check PASS marker validation SHALL be covered as separate behaviors
- **AND** a passing task artifact validation test SHALL NOT require a PASS verdict marker unless it is executing a check
