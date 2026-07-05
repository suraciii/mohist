### Requirement: Record label reads with metadata fallback are instance members of AgentSessionRecord

Reading a session label with fallback to session metadata SHALL be an instance method on `AgentSessionRecord` rather than an `internal static` on the core query service. The method SHALL resolve a key by consulting the record's own labels first and falling back to the session's metadata labels when the record does not carry the key, preserving the existing record-first-then-metadata resolution order. The core query class (`AgentSessionQuerier`) SHALL NOT expose a static `Label` accessor after this change.

#### Scenario: Record label takes precedence over session metadata

- **WHEN** a label key is present on both the record's labels and the session's metadata labels with different values
- **THEN** the instance accessor SHALL return the record's label value

#### Scenario: Fallback to session metadata when record label is absent

- **WHEN** a label key is absent from the record's labels but present on the session's metadata labels
- **THEN** the instance accessor SHALL return the session metadata label value

#### Scenario: Absent label returns null

- **WHEN** a label key is absent from both the record's labels and the session's metadata labels
- **THEN** the instance accessor SHALL return `null`

#### Scenario: Core query class no longer carries the static label accessor

- **WHEN** the core session query service class is inspected after the change
- **THEN** it SHALL NOT declare a static `Label(AgentSessionRecord, string)` member, and all sibling consumers SHALL call the record instance method

### Requirement: Issue-number parsing is an instance member of AgentSessionRecord

Parsing the issue number from the resolved issue-number label SHALL be an instance method on `AgentSessionRecord`. The method SHALL read the issue-number label (using the record-first-then-metadata fallback) and parse it as an integer, returning `0` when the label is absent or non-numeric. The core query class SHALL NOT expose a static `IssueNumber` accessor after this change.

#### Scenario: Numeric issue label is parsed

- **WHEN** the issue-number label resolves to a numeric string
- **THEN** the instance accessor SHALL return the parsed integer value

#### Scenario: Absent or non-numeric issue label yields zero

- **WHEN** the issue-number label is absent, empty, whitespace, or non-numeric
- **THEN** the instance accessor SHALL return `0`

#### Scenario: Core query class no longer carries the static issue-number accessor

- **WHEN** the core session query service class is inspected after the change
- **THEN** it SHALL NOT declare a static `IssueNumber(AgentSessionRecord)` member, and all sibling consumers SHALL call the record instance method

### Requirement: Annotation reads resolve directly on session metadata without a querier forwarder

The querier's `Annotation` forwarder SHALL be removed. Callers that previously routed through it SHALL read annotations directly from `session.Metadata.Annotation(key)`, because the forwarder was a pure pass-through with no added logic. No behavioral change SHALL be introduced by this removal.

#### Scenario: Callers read annotations directly from session metadata

- **WHEN** a caller needs an annotation value after the change
- **THEN** it SHALL invoke `session.Metadata.Annotation(key)` and observe the same value the former querier forwarder returned

#### Scenario: Core query class no longer carries the static annotation forwarder

- **WHEN** the core session query service class is inspected after the change
- **THEN** it SHALL NOT declare a static `Annotation(AgentSession, string)` member
