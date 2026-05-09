## ADDED Requirements

### Requirement: REQ-WD-001 Integrate owns intelligent OpenSpec spec sync

The workflow SHALL treat `integrate:spec-sync` as the stage task that writes approved change delta specs into main OpenSpec specs. The task SHALL read the change delta specs and existing main specs, resolve clear ADDED, MODIFIED, REMOVED, and RENAMED intent, and preserve separate integration steps for spec sync, archive, merge, and final health.

#### Scenario: Integrate runs distinct ordered steps
- **WHEN** an approved change enters INTEGRATE
- **THEN** the workflow SHALL run `integrate:spec-sync` before `integrate:archive-change`
- **AND** it SHALL keep `integrate:spec-sync`, `integrate:archive-change`, `integrate:merge`, and `final-health` as distinct task or step history entries

#### Scenario: Archive waits for spec sync
- **WHEN** `integrate:spec-sync` fails
- **THEN** the workflow SHALL NOT archive the OpenSpec change
- **AND** it SHALL NOT merge the candidate or run final health
