## Why

Task artifact expectations and check verdict markers currently share the same marker vocabulary, making it unclear whether a missing marker means an artifact shape problem or a failed PASS/FAIL verdict. Separating these concepts now prevents workflow failures from reporting check verdict failures as task artifact completion failures.

## What Changes

- Clarify task expectation schema and naming so tasks declare required artifact files and optional neutral artifact markers/content only.
- Keep PASS/FAIL verdict marker evaluation in check definitions and check execution paths.
- Update built-in workflow profile definitions to use artifact-focused task expectation language and verdict-focused check language.
- Improve runner diagnostics so missing artifact markers and failed or missing verdict markers produce distinct domain error messages.
- Add tests covering task file expectations, optional artifact markers, and check PASS marker validation as separate behaviors.

## Capabilities

### New Capabilities

- None.

### Modified Capabilities

- `workflow-definition`: Clarifies the declarative workflow contract so task expectations describe artifact requirements while check definitions own verdict marker requirements.
- `workflow-engine`: Changes runtime validation and diagnostics so task artifact validation and check PASS/FAIL verdict validation are evaluated and reported as separate domain concepts.

## Impact

- Affected code includes workflow profile definitions, task artifact expectation schema/types, task artifact validation, check verdict validation, and runner error message construction.
- Existing workflow behavior remains compatible at the stage level, but PASS/FAIL markers will no longer be modeled as task artifact completion requirements.
- No API, prompt, storage, or dependency changes are expected for this issue.
