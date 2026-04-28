## ADDED Requirements

### Requirement: Web unit test infrastructure with vitest
The system SHALL use vitest as the test runner for `packages/cli/web/` with jsdom environment, configured via `vite.config.ts` test block.

#### Scenario: Run web unit tests
- **WHEN** developer runs `npm test` in `packages/cli/web/`
- **THEN** vitest discovers and runs all test files matching `**/*.test.{ts,tsx}` under `src/`
- **AND** reports pass/fail results to stdout

### Requirement: Pure utility function unit tests
The system SHALL have unit tests for all pure utility functions in the web codebase. Each test SHALL assert exact expected values — no loose/partial assertions.

#### Scenario: Test time formatting utilities
- **WHEN** a time formatting function exists in a component (e.g. `formatTime`, `formatTimeAgo`)
- **THEN** there SHALL be a test file that extracts and tests the function with specific inputs
- **AND** assertions SHALL verify exact output strings for known inputs

#### Scenario: Test color/mapping constants
- **WHEN** a component uses hardcoded color mappings or label constants
- **THEN** there SHALL be a test that asserts the exact expected values
- **AND** any deviation (wrong hex code, wrong class name) SHALL cause test failure

### Requirement: Tests must prevent regressions found in Issue #30
The system SHALL have tests that would have caught the specific regressions from Issue #30: wrong color values and missing month format handling.

#### Scenario: Color value regression test
- **WHEN** a color constant or CSS class is changed to an incorrect value
- **THEN** at least one test SHALL fail

#### Scenario: Time format regression test
- **WHEN** a time formatting function loses a format case (e.g. month display)
- **THEN** at least one test SHALL fail
