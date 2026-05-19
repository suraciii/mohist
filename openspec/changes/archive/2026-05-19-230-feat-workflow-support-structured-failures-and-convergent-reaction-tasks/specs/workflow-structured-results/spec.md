## ADDED Requirements

### Requirement: REQ-WSR-001 Generic structured workflow results

Workflow task, check, and reaction outputs SHALL support generic structured result data without introducing review-specific core entities.

#### Scenario: Task and check outputs store generic items

- **WHEN** a task or check produces structured result data
- **THEN** Mohist SHALL store and read generic `items[]`, verdict, marker, evidence, summary, facts, snapshot, and verification fields when present
- **AND** each item SHALL support stable `id`, `severity`, `scope`, `evidence`, `suggestedAction`, and `verification` fields
- **AND** Mohist core SHALL NOT model these items as `ReviewFinding`, `ReviewSnapshot`, or any other review-specific entity

#### Scenario: Reaction outputs record item resolution

- **WHEN** a reaction task attempts to repair structured items
- **THEN** its output SHALL record attempted, resolved, unresolved, and newly observed item IDs with evidence
- **AND** older output records SHALL remain readable when these structured fields are absent

### Requirement: REQ-WSR-002 Declared verdict result contracts

AI judgment tasks SHALL be able to declare a result contract that derives PASS/FAIL from an explicit machine-readable marker in a declared output source.

#### Scenario: Promise marker contract parses a declared source

- **WHEN** a declared task artifact or structured task-output field contains exactly one allowed `<promise>PASS</promise>` or `<promise>FAIL</promise>` marker
- **THEN** Mohist SHALL parse the marker into a normalized PASS or FAIL verdict
- **AND** Mohist SHALL parse structured items and evidence from the same declared output envelope
- **AND** Mohist SHALL ignore markers in logs, transcripts, or unrelated artifacts

#### Scenario: Invalid marker output is an error

- **WHEN** the declared source is missing, contains no marker, contains duplicate markers, or contains a malformed marker
- **THEN** Mohist SHALL surface a clear task/check error
- **AND** Mohist SHALL NOT infer PASS or FAIL from natural-language prose

#### Scenario: Built-in judgment tasks share the contract

- **WHEN** review, self-review, plan-quality, or another built-in AI judgment task is checked
- **THEN** it SHALL use the shared verdict parser and result-contract error handling

### Requirement: REQ-WSR-003 Task-level self-repair policy

Task definitions SHALL be able to declare whether a task may perform bounded in-session repair before producing its final structured result.

#### Scenario: Built-in review repairs only safe local items

- **WHEN** the built-in review task finds a small, local, low-risk issue directly implied by the review
- **THEN** it MAY repair the item inside the task session when the self-repair policy permits it
- **AND** the final output SHALL record repaired item IDs, changed evidence, and verification results
- **AND** the final verdict SHALL be based on the post-repair candidate snapshot

#### Scenario: Unsafe or ambiguous items remain visible

- **WHEN** a finding affects product behavior, public contracts, migrations, data safety, security posture, merge strategy, architecture, scope, or requires user judgment
- **THEN** the review task SHALL report the item as unresolved, blocking, follow-up, pre-existing, or out-of-scope as appropriate
- **AND** it SHALL NOT silently repair the item or produce PASS without verification evidence

### Requirement: REQ-WSR-004 Comprehensive review structured output

The built-in review workflow SHALL use the generic structured result contract to perform a comprehensive pass rather than stopping after the first blocker.

#### Scenario: Review emits complete categorized items

- **WHEN** the built-in review task runs
- **THEN** it SHALL inspect current issue acceptance criteria, changed files, adjacent retry/recovery/artifact paths, stale evidence cases, and regression coverage gaps
- **AND** it SHALL emit exactly one verdict marker
- **AND** it SHALL separate directly repaired items, blocking current-change items, non-blocking follow-up items, and pre-existing or out-of-scope items

#### Scenario: Follow-up items do not block by default

- **WHEN** review reports follow-up or out-of-scope items
- **THEN** those items SHALL remain visible
- **AND** they SHALL NOT block the current workflow unless workflow policy classifies them as blocking
