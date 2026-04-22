## ADDED Requirements

### Requirement: CLI shows approval state
`mo issue show` SHALL display the issue's approval state when `approvalState` is present in the API response.

#### Scenario: Issue awaiting approval
- **WHEN** an issue has `approvalState.status: "awaiting"`
- **THEN** `mo issue show` output includes a line like `Approval: awaiting review (stage: plan)`
- **AND** includes self-review notes if present

#### Scenario: Issue approved
- **WHEN** an issue has `approvalState.status: "approved"`
- **THEN** `mo issue show` output includes a line like `Approval: approved (stage: plan)`

#### Scenario: Issue rejected
- **WHEN** an issue has `approvalState.status: "rejected"`
- **THEN** `mo issue show` output includes a line like `Approval: rejected (stage: plan)`

#### Scenario: Issue with no approval state
- **WHEN** an issue has no `approvalState`
- **THEN** no approval line is shown (current behavior preserved)
