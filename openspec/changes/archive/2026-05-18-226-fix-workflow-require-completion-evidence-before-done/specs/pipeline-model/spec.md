## MODIFIED Requirements

### Requirement: Done projection follows completed WorkflowRun evidence
Pipeline projection SHALL display Done only when WorkflowRun evidence proves the workflow passed through the final Integrate stage and SHALL defensively reject impossible passed snapshots.

#### Scenario: Passed snapshot before Integrate is rejected
- **WHEN** a WorkflowRun snapshot is marked passed but did not reach and pass the final Integrate stage
- **THEN** projection SHALL refuse to mark the issue Done
- **AND** it SHALL surface a diagnostic or blocked projection result

#### Scenario: Missing final evidence is rejected
- **WHEN** a WorkflowRun snapshot is marked passed but final-stage task, check, or delivery evidence is missing
- **THEN** projection SHALL refuse to mark the issue Done
- **AND** it SHALL not invent completion truth from issue stage, merge state, or session status

#### Scenario: Stale failed session does not override later workflow success
- **GIVEN** an older AgentSession failed
- **WHEN** the latest WorkflowRun evidence proves all required stages including Integrate completed successfully
- **THEN** projection SHALL allow Done despite the stale failed session

#### Scenario: Merge state alone is insufficient
- **WHEN** repository merge state indicates a merge or merged branch but WorkflowRun completion evidence is incomplete
- **THEN** projection SHALL not mark the issue Done from merge state alone
