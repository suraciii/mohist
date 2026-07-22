### Requirement: Built-in Definitions pass validation in CI

CI MUST run the authoritative validator over every built-in Workflow Definition and MUST fail the build if any built-in Definition is invalid.

#### Scenario: built-in profiles are golden cases
- **WHEN** CI runs on a change that touches a built-in Definition
- **THEN** the validator is executed against every built-in Definition and the build fails if any one is invalid

### Requirement: The documentation example passes validation in CI

CI MUST validate the complete Workflow Definition example published in the product reference, locking the documented syntax to the validator.

#### Scenario: docs example is a golden case
- **WHEN** CI runs
- **THEN** the complete example from the Workflow Definition documentation is validated and the build fails if it is invalid

### Requirement: An injected unknown field fails CI

CI MUST prove the validator catches unknown fields by injecting one into a golden-case Definition and asserting that validation fails with the expected error.

#### Scenario: negative case catches an unknown field
- **WHEN** CI injects an unknown field into an otherwise valid golden-case Definition
- **THEN** validation fails and the build reports the same unknown-field error the validator produces elsewhere

### Requirement: Skeleton and fragment snippets are excluded from validation

CI MUST NOT validate syntax skeletons that carry `<...>` placeholders or scattered documentation fragments, and those snippets MUST NOT produce false-positive failures.

#### Scenario: placeholder skeleton is not validated
- **WHEN** a documentation snippet contains `<...>` placeholders
- **THEN** CI does not run the validator against it and reports no failure

#### Scenario: scattered fragments are not validated
- **WHEN** a documentation snippet is an incomplete fragment rather than a complete Definition
- **THEN** CI does not run the validator against it and reports no failure
