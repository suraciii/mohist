## ADDED Requirements

### Requirement: Issue-level usage aggregation

The issue / workflow-run view SHALL surface an issue-level usage total that aggregates usage across the sessions belonging to the issue: at minimum the total token count and the total cost summed across the issue's sessions. The aggregate SHALL be derived from the same usage data the individual session rows report, so the total is consistent with the sum of its parts. The aggregate total SHALL be visible on the issue page alongside the workflow sessions panel, so a user can see at a glance what the issue cost across all of its sessions.

#### Scenario: Issue page shows total tokens and total cost

- **WHEN** the issue page renders for an issue that has one or more sessions with usage data
- **THEN** the page SHALL display the total token count aggregated across the issue's sessions
- **AND** the page SHALL display the total cost aggregated across the issue's sessions

#### Scenario: Aggregate is consistent with session rows

- **WHEN** the issue-level usage total is rendered alongside the workflow sessions panel
- **THEN** the aggregate total SHALL be consistent with the sum of the individual session rows' usage
- **AND** the aggregate SHALL NOT diverge from the per-session data shown in the same view

#### Scenario: No usage data does not render a misleading total

- **WHEN** the issue has no sessions with usage data
- **THEN** the issue page SHALL NOT render a misleading non-zero total
- **AND** the page MAY omit the aggregate region entirely

### Requirement: Realtime usage feed carries the complete usage payload

The realtime (SSE) usage feed that updates the workflow sessions panel SHALL deliver the complete usage payload on a `usage.updated` event, including `contextUsagePercent` and `healthStatus`, so the panel reflects live context-health updates without a full refetch. The `useWorkflowRunSessions` SSE handler SHALL apply every usage field the session usage read model carries, achieving parity with the `useCoderSessions` handler, and SHALL NOT silently drop `contextUsagePercent` or `healthStatus` from the update.

#### Scenario: SSE usage.updated carries context-usage percent and health status

- **WHEN** a `usage.updated` SSE event arrives for a workflow-run session
- **THEN** the workflow sessions panel SHALL update the session's `contextUsagePercent` from the event
- **AND** the panel SHALL update the session's `healthStatus` from the event
- **AND** the panel SHALL reflect the live context health without requiring a full refetch

#### Scenario: SSE handler parity with useCoderSessions

- **WHEN** the `useWorkflowRunSessions` SSE handler processes a `usage.updated` event
- **THEN** it SHALL apply the same set of usage fields that the `useCoderSessions` handler applies
- **AND** it SHALL NOT omit `contextUsagePercent` or `healthStatus` from the applied update
