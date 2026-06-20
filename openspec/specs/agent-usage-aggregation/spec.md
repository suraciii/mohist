# OpenSpec Capability: agent-usage-aggregation

### Requirement: Client activity-window usage snapshot aggregates token/cost totals

The Web client SHALL derive an activity-window usage snapshot by summing the additive usage fields (`inputTokens`, `outputTokens`, `totalTokens`, `costAmount`) from `useAgentActivity().sessions[].usage` over the sessions present in the activity window. The snapshot SHALL be computed client-side from the already-fetched activity payload and SHALL NOT trigger an additional network request. It is a lower-bound approximation scoped strictly to the sessions in the current activity window, not an all-time total.

#### Scenario: Totals are summed across activity-window sessions

- **WHEN** the activity payload contains sessions with usage `{ inputTokens: 100, outputTokens: 50, totalTokens: 150, costAmount: 0.02 }` and `{ inputTokens: 200, outputTokens: 80, totalTokens: 280, costAmount: 0.05 }`
- **THEN** the snapshot reports `inputTokens: 300`, `outputTokens: 130`, `totalTokens: 430`, `costAmount: 0.07`

#### Scenario: Missing or null usage fields are treated as zero

- **WHEN** a session in the activity window has no `usage` object, or has nullable/undefined additive fields
- **THEN** that session contributes zero to each additive total
- **AND** the snapshot computation SHALL NOT throw or surface an error

#### Scenario: Non-additive per-session fields are not aggregated

- **WHEN** sessions carry per-session point-in-time fields such as `contextWindowUsed`, `contextWindowSize`, `contextUsagePercent`, or `healthStatus`
- **THEN** the snapshot SHALL NOT sum those fields into a total
- **AND** only additive token/cost fields are aggregated

#### Scenario: Snapshot follows the activity window

- **WHEN** the activity window is limited to the most recent N sessions
- **THEN** the snapshot covers exactly those N sessions
- **AND** sessions outside the activity window SHALL NOT contribute to the totals even if they occurred in the same project

### Requirement: Snapshot scope is labeled as activity-window only

The Web UI that surfaces the usage snapshot SHALL display a scope label indicating that the totals reflect the activity window only and are a lower bound, so the reader does not mistake them for all-time or project-wide totals.

#### Scenario: Scope label is visible alongside the snapshot

- **WHEN** the usage snapshot is rendered
- **THEN** the UI SHALL present a visible "activity window only" scope qualifier next to the totals

#### Scenario: No all-time claim is implied

- **WHEN** a reader views the snapshot without the full activity window loaded
- **THEN** the UI SHALL NOT label the totals as project-total, weekly-total, or all-time

### Requirement: Server time-bucketed usage aggregation endpoint

The server SHALL expose an Agent/Session context endpoint at `GET /api/projects/{projectRef}/agent/usage` that returns token/cost usage totals bucketed by time. Each bucket SHALL report additive totals (`inputTokens`, `outputTokens`, `totalTokens`, `costAmount`) and the `costCurrency`. Buckets SHALL be ordered chronologically. The endpoint SHALL be additive and non-breaking.

#### Scenario: Endpoint returns chronologically ordered usage buckets

- **WHEN** the client requests `GET /api/projects/{projectRef}/agent/usage`
- **THEN** the response SHALL contain a list of time buckets ordered from earliest to latest
- **AND** each bucket SHALL carry a bucket boundary timestamp plus additive token/cost totals

#### Scenario: Bucket with no sessions is empty, not an error

- **WHEN** a time bucket has no sessions with usage in the fixed range
- **THEN** that bucket SHALL be present with zero totals
- **AND** the endpoint SHALL NOT omit the bucket or return an error

#### Scenario: Unauthenticated or unknown project is rejected

- **WHEN** the request targets a project that does not exist or the caller is not authorized
- **THEN** the endpoint SHALL return the same not-found/forbidden behavior as the other `/agent/*` routes

### Requirement: Time-series aggregation is built from persisted per-session usage

The time-bucketed aggregation SHALL be derived from the persisted per-session `AgentSession.Status.UsageSummary` (the same source as the per-session `usage` already exposed on activity cards), bucketed by session creation time. It SHALL cover completed as well as active sessions and SHALL NOT depend on live `usage.updated` transcript events. No new persistence store is introduced by this capability.

#### Scenario: Completed sessions contribute to the aggregation

- **WHEN** a session has completed and its persisted `UsageSummary` is present
- **THEN** that session's usage SHALL be included in the bucket matching its creation time
- **AND** the aggregation SHALL NOT require the session to be live

#### Scenario: A session without persisted usage contributes nothing

- **WHEN** a session has no persisted `UsageSummary`
- **THEN** the aggregation SHALL skip it without failing the request

### Requirement: v1 aggregation range, granularity, and currency are fixed

For v1, the aggregation endpoint SHALL use a fixed time range and a fixed bucket granularity, and SHALL NOT accept query parameters that change the range or granularity. The aggregation SHALL report `costAmount` summed as-is together with a single `costCurrency` and SHALL NOT perform multi-currency conversion.

#### Scenario: Range and granularity are not configurable

- **WHEN** the client sends range or granularity query parameters
- **THEN** the endpoint SHALL ignore them or reject them
- **AND** the response SHALL always cover the same fixed range and bucket size

#### Scenario: Cost is summed without currency conversion

- **WHEN** sessions report `costAmount` values in the same `costCurrency`
- **THEN** the bucket totals SHALL sum `costAmount` directly and echo that `costCurrency`
- **AND** no exchange-rate conversion SHALL be applied

### Requirement: Usage aggregation stays isolated from Issue completion metrics

Usage aggregation SHALL remain within the Agent/Session bounded context. It SHALL NOT share an endpoint with, or mix fields from, Issue completion-metric aggregation (issue context C). The usage response SHALL NOT include completion, stage-progress, or issue-status aggregate fields, and completion endpoints SHALL NOT include usage totals.

#### Scenario: Usage endpoint does not emit completion fields

- **WHEN** the usage aggregation endpoint responds
- **THEN** the response SHALL contain only Agent/Session usage fields (token/cost totals, bucket boundaries, currency)
- **AND** it SHALL NOT include issue completion, stage, or readiness metrics

#### Scenario: Completion-metric endpoints do not carry usage totals

- **WHEN** any Issue-context completion-metric aggregation is exposed
- **THEN** it SHALL NOT embed token/cost usage totals from this capability