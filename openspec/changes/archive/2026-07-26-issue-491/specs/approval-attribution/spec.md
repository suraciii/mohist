### Requirement: Every approval decision records a declarative operator

An approval decision (approve or reject) SHALL record the operator who placed the gate as a declarative `decidedBy` value, using the same model as the comment author: a declared name, not an authenticated identity. This lets an owner reading the approval history distinguish a gate placed by an agent from one placed by a person, which is the precondition for deciding whether to take over.

#### Scenario: Human approves with a declared author
- **WHEN** an operator approves a pending approval gate using `mo run approve --author supervisor`
- **THEN** the resulting approval decision SHALL carry `decidedBy = "supervisor"`, and the approval read model SHALL expose that operator.

#### Scenario: Agent approves with its declared author
- **WHEN** an agent approves a pending approval gate declaring its own author name
- **THEN** the resulting approval decision SHALL carry that agent-declared name as `decidedBy`, indistinguishable in shape from a human-declared one.

#### Scenario: Reject records the operator
- **WHEN** an operator rejects a pending approval gate declaring an author
- **THEN** the resulting approval decision SHALL carry that author as `decidedBy`.

### Requirement: The operator is required and validated like the comment author

The `decidedBy` value SHALL be required on every approve/reject decision, SHALL be trimmed of surrounding whitespace, and SHALL be rejected when blank or longer than 100 characters — mirroring the comment author validation. A decision without a valid operator SHALL be rejected rather than recorded with an empty operator.

#### Scenario: Missing author is rejected
- **WHEN** an approve or reject is attempted without supplying an author
- **THEN** the system SHALL reject the decision and SHALL NOT record an approval state change.

#### Scenario: Blank author is rejected
- **WHEN** an approve or reject is attempted with an author consisting only of whitespace
- **THEN** the system SHALL reject the decision.

#### Scenario: Overlong author is rejected
- **WHEN** an approve or reject is attempted with an author longer than 100 characters
- **THEN** the system SHALL reject the decision.

#### Scenario: Surrounding whitespace is trimmed
- **WHEN** an approve or reject is attempted with author `"  supervisor  "`
- **THEN** the recorded `decidedBy` SHALL be `"supervisor"`.

### Requirement: The operator is carried through the decision event and read model

The `decidedBy` value SHALL travel with the approval-resolution domain event and SHALL be present on the approval read model, so that issue and run histories expose who placed each gate without a separate lookup.

#### Scenario: Resolution event carries the operator
- **WHEN** an approval gate is resolved with an author
- **THEN** the stage approval resolution event SHALL include `decidedBy` set to that author.

#### Scenario: Read model exposes the operator
- **WHEN** the approval status of a resolved gate is read
- **THEN** the approval status view SHALL include `decidedBy` set to the recorded operator.

### Requirement: Historical approval data reads back without an operator

Approval data recorded before this change carries no `decidedBy`. Reading such historical data SHALL NOT fail and SHALL surface the operator as empty (or a system default), preserving compatibility with existing histories.

#### Scenario: Legacy approval read
- **WHEN** an approval gate resolved before this change is read back
- **THEN** the read model SHALL return successfully with `decidedBy` empty (or a system default), and SHALL NOT error.

### Requirement: Both CLI and HTTP surfaces accept the operator

`mo run approve` and `mo run reject` SHALL accept an `--author` option, and the corresponding HTTP endpoints (run-scoped and issue-scoped) SHALL accept the operator in the request body. The operator supplied at either surface SHALL become the `decidedBy` of the resulting decision.

#### Scenario: CLI supplies the author
- **WHEN** `mo run approve --author supervisor` is run against a pending gate
- **THEN** the request SHALL carry `author = "supervisor"` and the decision SHALL record `decidedBy = "supervisor"`.

#### Scenario: HTTP request supplies the author
- **WHEN** an approve or reject HTTP request supplies an `author` field
- **THEN** the decision SHALL record that author as `decidedBy`.
