## ADDED Requirements

### Requirement: Workflow profile variable clear via null on PATCH

`PATCH /api/issues/:number/workflow-profile/variables` SHALL treat a variable value of JSON `null` as an explicit removal of that key: after a successful PATCH that sets key `k` to `null`, a subsequent read of the issue's workflow profile variables SHALL NOT include `k`. This removal semantics SHALL coexist with the existing deep-merge behavior for present values — a PATCH that omits a key preserves it, a PATCH that includes a key with a value replaces/merges it, and a PATCH that includes a key with `null` removes it. This enables clients (including the `mo issue workflow config clear --var k` CLI verb) to delete individual variables against a deep-merge server that otherwise cannot drop keys.

#### Scenario: Null value removes a variable

- **WHEN** a client sends `PATCH /api/issues/:number/workflow-profile/variables` with `{ "foo": null }`
- **AND** the issue's variables previously contained `foo`
- **THEN** a subsequent `GET /api/issues/:number/workflow-profile` SHALL NOT include `foo` in variables
- **AND** other variables SHALL remain unchanged

#### Scenario: Absent key preserves a variable

- **WHEN** a client sends `PATCH /api/issues/:number/workflow-profile/variables` with a body that does not mention `foo`
- **AND** the issue's variables previously contained `foo`
- **THEN** `foo` SHALL remain present and unchanged in variables
