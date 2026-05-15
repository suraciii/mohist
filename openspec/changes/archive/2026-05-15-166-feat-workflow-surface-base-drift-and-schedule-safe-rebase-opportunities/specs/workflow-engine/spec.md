## MODIFIED Requirements

### Requirement: REQ-BDA-EVIDENCE-001 Check approval rejects stale base evidence

The workflow engine SHALL prevent Check approval from being requested or accepted when base drift makes review, merge-ready, or approval evidence stale.

#### Scenario: Drift invalidates Check approval evidence

- **WHEN** base drift is detected for an active Check issue
- **AND** the current approval evidence references an older base, merge base, or candidate snapshot
- **THEN** Check approval SHALL no longer be actionable
- **AND** Mohist SHALL instruct the user to rebase or rerun Check before approval can proceed

#### Scenario: Approval submit race is rejected

- **WHEN** a user submits Check approval
- **AND** the approval evidence is stale because the base advanced
- **THEN** Mohist SHALL reject the approval
- **AND** the issue SHALL NOT advance to Integrate from that stale evidence

#### Scenario: Rebase completion refreshes dependent evidence

- **WHEN** `rebase-branch` completes and changes candidate or base evidence
- **THEN** Check review, merge-ready, and approval state SHALL be invalidated or reset
- **AND** the affected evidence SHALL be regenerated before approval can be requested again

### Requirement: REQ-BDA-SAFE-WINDOW-001 Mutating work is protected from automatic rebase

The workflow engine SHALL only schedule automatic drift-driven rebase when the current WorkflowRun is at a safe window.

#### Scenario: Running mutating work defers rebase

- **WHEN** base drift is detected during a running mutating task
- **THEN** Mohist SHALL NOT schedule `rebase-branch` immediately
- **AND** the rebase opportunity SHALL record a defer reason

#### Scenario: Task boundary reconsiders deferred opportunity

- **WHEN** a mutating task completes after drift was deferred
- **THEN** Mohist SHALL re-evaluate the rebase opportunity
- **AND** the opportunity SHALL become suggestible or schedulable if the new state is a safe window
