### Requirement: Issue-title batch lookup resides on the Issue read side

The issue-title batch lookup and its single-title fallback resolver SHALL reside on the Issue read side (`Issue/Services/`) rather than on the core session query class as `internal static` members. The Session domain SHALL consume this capability by invoking the Issue read-side service/method for a `(project, issueNumbers)` tuple. The core query class (`AgentSessionQuerier`) SHALL NOT declare `LoadIssueTitlesAsync` or `IssueTitle` as `internal static` members after this change.

#### Scenario: Core query class exposes no issue-title statics

- **WHEN** the core session query service class is inspected after the change
- **THEN** it SHALL NOT declare `LoadIssueTitlesAsync` or `IssueTitle` as `internal static` members

### Requirement: Issue-title batch lookup produces identical titles after relocation

The relocated batch lookup SHALL load issue rows for the given project and distinct issue numbers, map them via the issue row mapper, and return a number → title dictionary. The result SHALL be identical to the pre-change lookup for every `(project, numbers)` input, including empty-input cases.

#### Scenario: Empty issue-number input yields an empty dictionary

- **WHEN** the batch lookup is invoked with no issue numbers
- **THEN** it SHALL return an empty dictionary without querying the database

#### Scenario: Distinct numbers are deduplicated before lookup

- **WHEN** the batch lookup is invoked with duplicate issue numbers
- **THEN** each distinct number SHALL be looked up once and the result SHALL contain one entry per resolved number

#### Scenario: Session and activity feed consumers share the same titles

- **WHEN** both the core query service and the activity feed assembler look up titles for the same set of issue numbers in the same project
- **THEN** they SHALL obtain identical number → title maps by invoking the same Issue read-side capability

### Requirement: Single-title fallback preserves the Issue #{n} semantics

The single-title resolver SHALL return the stored title when the number maps to a non-whitespace title, and SHALL fall back to the literal `Issue #{number}` string otherwise. This fallback SHALL be byte-identical to the pre-change resolver.

#### Scenario: Stored title is returned when present

- **WHEN** the resolver is invoked for a number that maps to a non-whitespace title
- **THEN** it SHALL return the stored title verbatim

#### Scenario: Missing or blank title falls back to Issue #{n}

- **WHEN** the resolver is invoked for a number that is absent from the titles map or maps to a whitespace title
- **THEN** it SHALL return the literal string `Issue #{number}` where `{number}` is the issue number
