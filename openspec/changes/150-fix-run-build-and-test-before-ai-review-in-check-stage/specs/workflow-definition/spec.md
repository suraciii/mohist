## MODIFIED Requirements

### Requirement: Check stage behavior

The check stage SHALL run build/test verification before AI review artifact generation, AI review checks, or user approval.

#### Scenario: Build/test runs before AI review artifacts

- **WHEN** the check stage starts
- **THEN** the system SHALL run `BuildTestCheck` using the configured `checks.buildTest` command and timeout
- **AND** the system SHALL NOT generate `review.md` or `review-self-check.md` before `BuildTestCheck` passes

#### Scenario: Build/test failure with autofix succeeds

- **WHEN** `BuildTestCheck` fails
- **AND** a configured autofix attempt changes the implementation
- **THEN** the system SHALL rerun the same build/test command
- **AND** if the rerun passes, the check stage SHALL continue to AI review artifact generation and AI review checks

#### Scenario: Build/test failure after max autofix attempts

- **WHEN** `BuildTestCheck` still fails after the maximum autofix attempts
- **THEN** the check stage SHALL stop with a failed result
- **AND** the result SHALL include a concise failure summary and useful build/test log excerpt
- **AND** the system SHALL NOT generate `review.md` or `review-self-check.md`
- **AND** the system SHALL NOT request user approval

#### Scenario: AI review and approval after mechanical verification

- **WHEN** `BuildTestCheck` passes
- **THEN** the system SHALL generate or reuse AI review artifacts
- **AND** the system SHALL run the existing AI review checks
- **AND** the system SHALL request user approval only after AI review passes

#### Scenario: Existing AI review behavior preserved

- **WHEN** build/test verification passes and AI review runs
- **THEN** existing AI review verdict parsing, autofix handling, and failure handling SHALL continue to apply
