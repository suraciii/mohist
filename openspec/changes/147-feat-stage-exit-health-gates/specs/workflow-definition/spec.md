## MODIFIED Requirements

### Requirement: stage health gates before approval

Workflow stages SHALL run configured health gates before any user approval check at that stage boundary. A stage SHALL NOT request approval or advance as completed while an enabled health gate for that boundary is failing.

#### Scenario: Plan approval waits for plan health gate
- **WHEN** plan artifacts and self-review checks pass
- **AND** the plan health gate is enabled
- **THEN** the plan health gate SHALL run before plan user approval is requested
- **AND** approval SHALL NOT be requested if the plan health gate fails

#### Scenario: Build completion includes build health gate
- **WHEN** all build tasks are complete
- **AND** the build health gate is enabled
- **THEN** build stage completion SHALL require the build health gate to pass
- **AND** the issue SHALL NOT advance to check while the build health gate fails

#### Scenario: Check approval waits for full verification gate
- **WHEN** check stage reaches verification
- **AND** the check health gate is enabled
- **THEN** the check health gate SHALL run before AI review and user approval
- **AND** check approval SHALL NOT be requested if the check health gate fails

### Requirement: health gate failure visibility in stage execution

Health gate results SHALL be stored as stage execution check results using the existing check result persistence path.

#### Scenario: Health gate result is persisted
- **WHEN** a health gate runs during plan, build, or check
- **THEN** the corresponding stage execution SHALL include a check result named for that health gate
- **AND** the result SHALL include command, duration, enabled status, concise summary, and bounded log excerpt when applicable
