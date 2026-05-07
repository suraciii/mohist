## MODIFIED Requirements

### Requirement: REQ-CLI-001 Issue show displays merge delivery state

`mo issue show` SHALL display the issue's merge delivery state, including null merge states and false-done anomalies.

#### Scenario: Show merged issue
- **GIVEN** an issue has `mergeState=merged`
- **WHEN** the user runs `mo issue show <number>`
- **THEN** the output SHALL show that the issue is merged
- **AND** show source and target branch context when available

#### Scenario: Show false-done issue
- **GIVEN** an issue has `stage=done`
- **AND** `status=completed`
- **AND** `mergeState` is null or not `merged`
- **WHEN** the user runs `mo issue show <number>`
- **THEN** the output SHALL show a warning that the issue is done but not merged

### Requirement: REQ-CLI-002 Approve output reflects server action

`mo issue approve` SHALL print the action message returned by the server instead of a fixed resumed-agent message.

#### Scenario: Approve Plan approval
- **GIVEN** the server response message says the issue was approved and enqueued for resume-pipeline
- **WHEN** `mo issue approve <number>` succeeds
- **THEN** the CLI output SHALL include that message

#### Scenario: Approve Check approval
- **GIVEN** the server response message says the issue was approved and enqueued for merge
- **WHEN** `mo issue approve <number>` succeeds
- **THEN** the CLI output SHALL include that message
