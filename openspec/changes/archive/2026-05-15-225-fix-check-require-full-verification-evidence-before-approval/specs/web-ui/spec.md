## MODIFIED Requirements

### Requirement: Web UI shows Check verification approval blockers

The Web UI SHALL make failed or missing Check full verification evidence visible before approval instead of presenting the issue as merely waiting for user approval.

#### Scenario: Failed verification is visible on issue detail

- **WHEN** an issue is in Check and `health:check` has failed
- **THEN** the issue detail or approval panel SHALL show the failed Check verification gate
- **AND** it SHALL show the command, summary, duration, and log excerpt when available

#### Scenario: Approval panel indicates verified candidate

- **WHEN** Check approval is available
- **THEN** the approval panel SHALL indicate that required full verification evidence passed for the approval candidate
