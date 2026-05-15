## MODIFIED Requirements

### Requirement: Issue show exposes Check verification approval blockers

`mo issue show <number>` SHALL surface failed or missing Check full verification evidence when it blocks Check approval.

#### Scenario: Failed Check verification appears in issue show

- **WHEN** Check full verification fails for an issue
- **THEN** `mo issue show <number>` SHALL show the failed Check verification gate
- **AND** it SHALL include command, summary, duration, and log excerpt when available

#### Scenario: Missing Check verification explains unavailable approval

- **WHEN** Check approval is unavailable because full verification evidence is missing
- **THEN** `mo issue show <number>` SHALL show that approval is blocked by missing Check verification evidence
