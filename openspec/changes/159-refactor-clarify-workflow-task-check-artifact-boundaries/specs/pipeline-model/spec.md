## MODIFIED Requirements

### Requirement: REQ-PM-001 Stage task check boundaries are explicit

Pipeline stages SHALL follow clear responsibility boundaries: stages orchestrate, tasks execute, and checks verify. Only tasks SHALL be allowed to change code, write durable workflow artifacts, run coder sessions, run side-effecting commands, or repair failed check findings.

#### Scenario: Stage runs task check loop
- **WHEN** a stage executes
- **THEN** it SHALL run its tasks
- **AND** it SHALL run its checks
- **AND** if a check fails with a configured fix task, it SHALL run that fix task and re-run the failed check
- **AND** if the check still fails after max attempts, it SHALL stop in the current stage

#### Scenario: Check does not execute repair
- **WHEN** a failed check identifies a repairable problem
- **THEN** the repair SHALL be represented as a task in the same stage history
- **AND** the check SHALL remain a read-only verifier

### Requirement: REQ-PM-002 No fallback chain for first fix policy

The first check failure policy implementation SHALL NOT introduce fallback-to-plan, fallback-to-build, fallback ask-user, nested reaction chains, or multi-stage failure policies. When fix attempts are exhausted, the stage SHALL remain failed or paused with visible evidence.

#### Scenario: Exhausted fix attempts do not change stage
- **WHEN** a check fails after all configured fix attempts
- **THEN** the issue SHALL remain in the current stage state for user or later workflow recovery
- **AND** the failed check result and fix task result SHALL remain visible
