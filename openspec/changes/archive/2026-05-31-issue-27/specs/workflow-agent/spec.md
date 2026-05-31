## ADDED Requirements

### Requirement: Active agent ownership agrees with workflow leases
Agent session read models, agent activity APIs, and agent status APIs SHALL agree with the active `WorkflowLease` owner for a running workflow work item. The system MUST NOT report one runner as the active session owner while the durable workflow lease for the same work item names a different runner.

#### Scenario: Activity owner matches active lease
- **WHEN** `/api/agent/activity` reports a running workflow work item
- **THEN** the reported runner owner SHALL match the active `WorkflowLease` runner for that workflow work item
- **AND** the reported work item id SHALL match the leased work item id

#### Scenario: Session read model follows lease handoff
- **WHEN** workflow lease ownership changes through explicit abandonment, expiration, retry, recovery, or handoff
- **THEN** the workflow agent session read model SHALL reflect the new active owner only after the ownership transition is durable
- **AND** stale sessions from the prior owner SHALL no longer be reported as the active owner for that work item

#### Scenario: Mismatched owner is reconciled before reporting active state
- **WHEN** durable workflow lease state and workflow agent session state disagree about the runner owner of a running work item
- **THEN** agent activity and status reads SHALL reconcile or surface the inconsistency as a recovery state
- **AND** they SHALL NOT present both owners as simultaneously valid active executors
