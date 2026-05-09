## MODIFIED Requirements

### Requirement: REQ-WA-001 Workflow consumes session results without judging liveness

Workflow orchestration SHALL consume completed, failed, or cancelled session call results from tasks and SHALL NOT independently determine whether opencode is alive.

#### Scenario: Workflow receives session failure
- **WHEN** a task reports that its opencode session failed
- **THEN** workflow SHALL handle that as a task/session execution result
- **AND** workflow SHALL decide retry, block, interruption, or user action through existing workflow policy

#### Scenario: Session state does not mutate issue state directly
- **WHEN** a session enters `probing` or `failed`
- **THEN** issue `stage` and `status` SHALL remain unchanged unless a separate workflow decision changes them later
