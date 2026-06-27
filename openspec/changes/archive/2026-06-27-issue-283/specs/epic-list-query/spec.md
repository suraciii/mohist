## MODIFIED Requirements

### Requirement: Epic list progress correctness is preserved

The epic list endpoint SHALL produce `deliveredCount`, `totalIssueCount`, and `readyToMarkDone` identical to the full-enrichment path, derived from the linked issues' stored `Status`. A linked issue SHALL count as delivered only when its `Status` is `done` or `completed`; a `cancelled` linked issue SHALL NOT count toward `deliveredCount`. `readyToMarkDone` SHALL be `true` when the epic has at least one linked issue and **no open linked issue** (i.e. every linked issue is terminal — `done`/`completed` or `cancelled`), reusing the same shared readiness rule as auto-done, manual "Mark Done", and resume re-evaluation. A `cancelled` linked issue SHALL NOT count as delivered, but SHALL NOT by itself keep `readyToMarkDone` false.

#### Scenario: List progress matches the detail path

- **WHEN** the epic list endpoint computes progress for an epic
- **THEN** `deliveredCount`, `totalIssueCount`, and `readyToMarkDone` SHALL equal the values the full-enrichment detail path would produce for the same epic

#### Scenario: Cancelled issues are not counted as delivered

- **WHEN** a linked issue is `cancelled`
- **THEN** it SHALL NOT increment `deliveredCount`
- **AND** SHALL NOT be treated as delivered for any purpose

#### Scenario: Cancelled-only-remaining epic is ready to mark done

- **WHEN** an epic has at least one linked issue and every linked issue is terminal (`done`/`completed` or `cancelled`)
- **AND** at least one linked issue is `cancelled`
- **THEN** `readyToMarkDone` SHALL be `true`
- **AND** `deliveredCount` SHALL count only the `done`/`completed` linked issues

#### Scenario: Open linked issue blocks readyToMarkDone

- **WHEN** an epic has at least one open linked issue (`backlog`, `draft`, `in_progress`, `blocked`, or `paused`)
- **THEN** `readyToMarkDone` SHALL be `false`

#### Scenario: Empty epic and archived issues

- **WHEN** an epic has no linked issues, or all linked issues are archived
- **THEN** `totalIssueCount` and `deliveredCount` SHALL be zero
- **AND** `readyToMarkDone` SHALL be false
