### Requirement: The Dashboard is an attention/pulse surface and SHALL NOT render a Productivity or charts zone

The Dashboard page SHALL be an attention-and-pulse surface and SHALL NOT render a productivity zone or any trending-chart zone. The `Productivity` zone slot and the `ProductivityZone` component SHALL be removed from the Dashboard composition, and the Dashboard SHALL NOT mount any of the migrated trend charts (throughput, completion-trend, cumulative-flow, cycle-time, stage-duration, quality, ftr-trend, investment, cost-trend, or the epic-progress list). Trending visualizations SHALL live exclusively on `/insights`. This narrowing is a UI-only change; it SHALL NOT alter any backend, API, or data contract.

#### Scenario: The Dashboard renders no productivity zone

- **WHEN** the Dashboard page renders for a project
- **THEN** the page SHALL NOT render a zone with the `productivity` identity
- **AND** the page SHALL NOT mount the `ProductivityZone` component

#### Scenario: The Dashboard renders no migrated trend chart

- **WHEN** the Dashboard page renders for a project
- **THEN** none of the migrated trend charts (ThroughputChart, CompletionTrend, CumulativeFlowChart, CycleTimeChart, StageDurationChart, QualityPanel, FtrTrendChart, InvestmentPanel, CostTrendChart, EpicProgressList) SHALL render on the Dashboard
- **AND** the Dashboard SHALL NOT render a charts zone

#### Scenario: Trending charts are reachable only on Insights

- **WHEN** the Dashboard productivity zone is removed
- **THEN** the trending visualizations SHALL be reachable exclusively on the `/insights` page
- **AND** the Dashboard SHALL NOT duplicate any migrated chart

### Requirement: The Dashboard headline, AttentionHero, Pulse zone, and Digest zone are unaffected by the productivity-zone removal

The removal of the Productivity zone SHALL NOT alter the Dashboard's factory-status headline, the full-width `AttentionHero`, the `Pulse` zone, or the `Digest` zone. These zones SHALL continue to render in their existing positions and SHALL retain their existing content and behavior. Only the Productivity zone is removed; the surviving zones SHALL NOT be regressed.

#### Scenario: Headline and Hero remain

- **WHEN** the Dashboard page renders after the Productivity zone is removed
- **THEN** the factory-status headline slot SHALL render at the top spanning the full content width
- **AND** the `AttentionHero` slot SHALL render full-width directly below the headline

#### Scenario: Pulse and Digest zones remain functional

- **WHEN** the Dashboard page renders after the Productivity zone is removed
- **THEN** the `Pulse` zone SHALL render and SHALL retain its existing content and behavior
- **AND** the `Digest` zone SHALL render and SHALL retain its existing content and behavior

#### Scenario: No backend, API, or data contract is changed

- **WHEN** the Productivity zone is removed from the Dashboard
- **THEN** no backend endpoint, API route, or data contract SHALL be altered
- **AND** the data hooks shared with `/insights` SHALL remain reusable as-is
