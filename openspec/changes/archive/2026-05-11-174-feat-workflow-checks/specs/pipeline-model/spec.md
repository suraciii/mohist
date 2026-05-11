## MODIFIED Requirements

### Requirement: Collected check evidence remains visible through repair

Pipeline stage execution SHALL preserve the complete initial check evidence for a phase even when a later repair task is attempted. Repair handling may change the current effective result, but it SHALL NOT reduce the user's visibility back to only the first discovered failure.

#### Scenario: Repairable failure still shows full initial diagnosis

- **WHEN** a phase initially reports multiple failing non-approval checks
- **AND** the earliest repairable failure triggers a fix task
- **THEN** the phase history SHALL still show the full initial collected result set
- **AND** the fix task plus recheck results SHALL be visible alongside that baseline evidence

#### Scenario: Later checks rerun after successful repair

- **WHEN** a fix task makes the targeted failing check pass on recheck
- **THEN** the workflow SHALL continue running later checks from that point using the repaired state
- **AND** it SHALL preserve the existing semantic that downstream checks are not skipped forever after an earlier repair succeeds

### Requirement: Exhausted or unrepairable failures remain local with full evidence

When collected phase failures cannot be repaired or remain failing after allowed attempts, the workflow SHALL stay in the current stage with complete evidence visible. It SHALL NOT fall back to another stage or collapse the visible diagnosis back to the first failure only.

#### Scenario: Failure without policy remains local

- **WHEN** a collected non-approval check result is `fail` or `error`
- **AND** no `CheckFailurePolicy` exists for that check
- **THEN** the workflow SHALL keep the issue in the current stage state
- **AND** the collected phase evidence SHALL remain visible to the user

#### Scenario: Exhausted repair attempts preserve evidence

- **WHEN** a collected failed or errored non-approval check has a fix policy
- **AND** the check still does not pass after the configured max attempts
- **THEN** the workflow SHALL keep the failed check results and fix task results visible
- **AND** it SHALL NOT automatically fall back to plan, build, or another escalation path
