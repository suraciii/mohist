## ADDED Requirements

### Requirement: Authoritative AI review result

The workflow engine SHALL maintain exactly one authoritative AI review result for the current check cycle. When an AI review failure is followed by a fix task and re-review, the re-review result SHALL replace the previous current AI review truth before approval can be requested.

#### Scenario: Re-review PASS replaces stale FAIL

- **WHEN** `ai-review` fails
- **AND** `fix-review-findings` completes
- **AND** the regenerated re-review returns PASS
- **THEN** the current persisted `ai-review` result SHALL be PASS
- **AND** issue detail, check state, and approval output SHALL NOT expose the earlier FAIL as the current result

#### Scenario: Approval uses latest AI review result

- **WHEN** check-stage approval output is built
- **AND** multiple historical `ai-review` attempts exist in the stage execution history
- **THEN** approval output SHALL use the latest authoritative `ai-review` result
- **AND** it SHALL NOT use an older failed attempt selected by first-match lookup

#### Scenario: Re-review FAIL remains blocking truth

- **WHEN** `ai-review` fails
- **AND** `fix-review-findings` completes
- **AND** the regenerated re-review still returns FAIL
- **THEN** the current persisted `ai-review` result SHALL be the latest FAIL
- **AND** the check stage SHALL NOT request ordinary user approval

#### Scenario: Authoritative result includes snapshot metadata

- **WHEN** an authoritative `ai-review` result is persisted
- **THEN** its output SHALL include the verdict, review report, reviewed snapshot SHA, review artifact path, and self-check artifact path when available
- **AND** downstream approval and display code SHALL read from that authoritative result
