## MODIFIED Requirements

### Requirement: Attention Hero derives content exclusively from existing read-only sources

The Attention Hero SHALL derive its content from existing frontend data sources — the active issue query (`useIssues`) and the agent status query (`useAgentStatus`) — with the single exception of the **summary approval-wait metric**, which the Hero SHALL consume from the `approval-waiting-metrics` aggregation endpoint. The only mutations the Hero performs SHALL be the existing issue-approval and issue-resume actions triggered by the inline Approve/Resume controls. No backend API endpoint other than the approval-wait aggregation endpoint SHALL be introduced to support the Hero, and no domain state beyond those existing actions SHALL be added. Every other Hero behavior (attention types, inline actions, one-line contexts) SHALL remain read-only over the existing issue and agent-status sources.

#### Scenario: Only the approval-wait aggregation is added as a new source

- **WHEN** the Attention Hero renders and refreshes its data
- **THEN** the Hero SHALL consume the existing issue and agent-status sources
- **AND** the Hero MAY additionally consume the approval-wait aggregation endpoint solely for the summary approval-wait metric
- **AND** no backend API endpoint other than the approval-wait aggregation endpoint SHALL be added to support the Hero

#### Scenario: Only existing approve/resume actions mutate state

- **WHEN** a user invokes an inline action in the Attention Hero
- **THEN** the only mutations SHALL be the existing issue-approval and issue-resume actions
- **AND** no new write operation against domain state SHALL be introduced

## ADDED Requirements

### Requirement: Attention Hero displays the summary approval-wait metric

The Attention Hero SHALL display a summary approval-wait metric (e.g. "your approvals averaged 3.2h") sourced from the `approval-waiting-metrics` aggregation, so the "is the human a bottleneck" signal lives where the user is about to act rather than forcing them to compute it from the full issue list. The displayed statistic SHALL be the aggregate average approval waiting time over the trailing 7-day window, computed server-side; the Hero SHALL NOT compute the metric client-side over the full issue list. The Hero SHALL render a defined empty/zero-sample presentation when the aggregation returns no samples, so the absence of data is distinguishable from a short wait.

#### Scenario: Hero shows the average approval wait from the aggregation

- **WHEN** the Attention Hero renders and the approval-wait aggregation returns at least one completed approval within the trailing 7-day window
- **THEN** the Hero SHALL display the aggregate average approval waiting time
- **AND** the displayed value SHALL be sourced from the approval-wait aggregation endpoint
- **AND** the Hero SHALL NOT compute the metric client-side over the full issue list

#### Scenario: Hero renders a defined empty presentation when there are no samples

- **WHEN** the approval-wait aggregation returns no completed approvals within the trailing 7-day window
- **THEN** the Attention Hero SHALL render a defined empty/zero-sample presentation
- **AND** the empty presentation SHALL be distinguishable from a genuine short wait time

#### Scenario: Summary metric excludes pending approvals

- **WHEN** the Attention Hero displays the summary approval-wait metric
- **THEN** the metric SHALL exclude pending (`awaiting`) approvals from the aggregate, consistent with the `approval-waiting-metrics` capability
- **AND** pending approvals SHALL continue to surface as individual attention items rather than within the summary wait time
