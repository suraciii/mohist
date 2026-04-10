## MODIFIED Requirements

### Requirement: Issue-Level Pending Approval Query

IssueRepo SHALL provide a method to query pending approval state by issue ID, not just by project ID.

#### Scenario: find pending approval by issue ID
- **WHEN** findPendingApprovalByIssueId(issueId) is called
- **THEN** it SHALL return the Issue if its approval_state.status equals "awaiting", or null otherwise

### Requirement: Duplicate Execution Prevention

AgentRunnerService.start() SHALL reject starting a new agent for an issue that has a pending approval.

#### Scenario: start rejected when approval pending
- **WHEN** start() is called for an issue with approval_state.status === "awaiting"
- **THEN** it SHALL return { started: false } with an error message instructing the user to use resume or submit approval first

#### Scenario: start allowed when no approval pending
- **WHEN** start() is called for an issue with no pending approval
- **THEN** it SHALL proceed with normal agent execution
