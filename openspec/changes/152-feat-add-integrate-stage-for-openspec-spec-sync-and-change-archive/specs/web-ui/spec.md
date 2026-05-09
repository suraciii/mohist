## MODIFIED Requirements

### Requirement: integrate stage visibility
The Web UI SHALL display Integrate as a distinct stage between Check and Done.

#### Scenario: Issue is integrating
- **WHEN** an issue is in `integrate`
- **THEN** issue cards and detail pages show it as integrating or integration failed
- **AND** they do not display it as Done

#### Scenario: Check approval shows readiness
- **WHEN** Check is awaiting approval and readiness data is available
- **THEN** the approval panel shows spec impact, sync dry-run status, merge readiness, target/base/head SHAs, and final health policy

#### Scenario: Integrate detail shows steps
- **WHEN** the user views an issue in Integrate
- **THEN** the UI displays Sync main specs, Archive OpenSpec change, Merge to target branch, Run final integration health gate, and Complete issue steps with current status

#### Scenario: Integrate failure is actionable
- **WHEN** Integrate fails
- **THEN** the UI shows failing step, relevant capability or conflicted files, requirement header or merge reason, health command details when available, and next-action guidance
