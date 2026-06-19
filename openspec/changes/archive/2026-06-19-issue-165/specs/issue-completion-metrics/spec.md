## ADDED Requirements

### Requirement: Client snapshot derives rolling 7-day completion counts

A standalone client-side derivation function SHALL compute, from the issues already loaded by the issue list query, three counts for the most recent rolling 7-day window: `completed` (issues currently in the `done` terminal status), `failed` (issues currently in the `cancelled` terminal status), and `new` (issues whose `createdAt` falls within the window). The function SHALL read only issue `status`, `createdAt`, and `updatedAt`. For the snapshot, the time at which an issue reached a terminal status SHALL be approximated by its `updatedAt`; this approximation is an accepted v1 limitation for the snapshot only.

#### Scenario: Counts reflect terminal statuses in the window

- **WHEN** the loaded issues contain three issues with status `done` and `updatedAt` within the last 7 days, two issues with status `cancelled` and `updatedAt` within the last 7 days, and five issues with `createdAt` within the last 7 days
- **THEN** the snapshot SHALL return `completed: 3`, `failed: 2`, and `new: 5`

#### Scenario: Issues outside the window are excluded

- **WHEN** an issue has status `done` but its `updatedAt` is older than 7 days
- **THEN** the snapshot SHALL NOT count it in `completed`
- **AND** an issue whose `createdAt` is older than 7 days SHALL NOT be counted in `new`

#### Scenario: Non-terminal issues are not counted as completed or failed

- **WHEN** an issue has status `backlog` or `in_progress`
- **THEN** the snapshot SHALL NOT count it in `completed` or `failed`
- **AND** the issue SHALL still be counted in `new` when its `createdAt` falls within the window

### Requirement: Client snapshot is a standalone replaceable derivation

The snapshot SHALL be a pure, standalone function with a stable signature and location under `packages/web/src/entities/issue/`. It SHALL consume an already-loaded issue collection rather than performing its own fetch, so that its callers can be swapped to an endpoint-backed derivation once the backend aggregation is ready. The function's return shape (the `completed` / `failed` / `new` counts) SHALL remain stable across the client-approximation and endpoint-backed implementations, preserving a reservation contract (signature and location) for the later swap.

#### Scenario: Snapshot does not perform its own data fetch

- **WHEN** the snapshot function is invoked
- **THEN** it SHALL accept the loaded issue collection as input
- **AND** it SHALL NOT issue a network request or call the issue list query directly

#### Scenario: Return shape is preserved for backend swap

- **WHEN** the snapshot is later replaced by an endpoint-backed derivation
- **THEN** the return shape exposed to consumers SHALL remain the `completed`, `failed`, and `new` counts
- **AND** consumers SHALL not change their usage of the counts

### Requirement: Server aggregation endpoint returns completion count buckets

A new read-only Issue-context HTTP aggregation endpoint SHALL return a time series of completion-count buckets for the current project. Each bucket SHALL carry its time boundary and a count of issues that reached a terminal state within that boundary. v1 SHALL support fixed by-day and by-week bucketing. The endpoint SHALL NOT expose configurable bucket size, custom time range, prediction, or regression.

#### Scenario: Aggregation returns by-day buckets

- **WHEN** a client requests the aggregation endpoint with by-day bucketing for a project
- **THEN** the response SHALL return one bucket per day within the covered window
- **AND** each bucket SHALL include the day boundary and the count of issues that reached a terminal state on that day

#### Scenario: Aggregation returns by-week buckets

- **WHEN** a client requests the aggregation endpoint with by-week bucketing for a project
- **THEN** the response SHALL return one bucket per week within the covered window
- **AND** each bucket SHALL include the week boundary and the count of issues that reached a terminal state during that week

#### Scenario: Fixed bucketing rejects custom configuration

- **WHEN** a client requests the aggregation endpoint with a custom bucket size or custom time range
- **THEN** the endpoint SHALL NOT apply arbitrary custom bucketing
- **AND** v1 SHALL only honor the fixed by-day and by-week options

### Requirement: Server aggregation uses correct completion-time semantics

The aggregation endpoint SHALL bucket each issue by the time it actually reached its terminal state. It SHALL NOT use issue `updatedAt` as the completion time, because `updatedAt` is touched on every change and misattributes issues edited in one period but completed in another. The completion time SHALL be the most precise terminal-state timestamp available in issue/workflow persistence; when the Issue aggregate does not record a dedicated completion timestamp, the endpoint SHALL derive it from the workflow run completion event rather than from `updatedAt`.

#### Scenario: Issue edited after completion is attributed to its completion period

- **WHEN** an issue reached its terminal state in week 1 but its `updatedAt` falls in week 2
- **THEN** the aggregation endpoint SHALL count the issue in the week 1 bucket
- **AND** it SHALL NOT count the issue in the week 2 bucket

#### Scenario: Completion time derived from workflow run when no dedicated timestamp exists

- **WHEN** the Issue aggregate has no dedicated completion timestamp field
- **THEN** the aggregation endpoint SHALL derive the completion time from the workflow run completion event
- **AND** it SHALL NOT fall back to issue `updatedAt` as the completion time

### Requirement: Completion metrics exclude AgentActivity as a source

Completion counts produced by both the client snapshot and the server aggregation endpoint SHALL be derived from issue and workflow completion facts only. They SHALL NOT use `AgentActivity.summary.completed` or `AgentActivity.summary.failed` as a source, because those values represent the current activity-window count rather than historical completion.

#### Scenario: Counts are not sourced from AgentActivity summary

- **WHEN** completion counts are computed by the snapshot or the aggregation endpoint
- **THEN** the computation SHALL read only issue and workflow completion facts
- **AND** it SHALL NOT read `AgentActivity.summary.completed` or `AgentActivity.summary.failed`
