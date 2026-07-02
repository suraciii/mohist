### Requirement: The Insights page renders the migrated trend charts in exactly four fixed dimension groups below the Signal Summary

The `/insights` page SHALL render the migrated trend charts below the Signal Summary, organized into exactly four fixed dimension groups. The four groups SHALL be, in this fixed order: 产出 (Output), 交付效率 (Delivery Efficiency), 质量 (Quality), and 投入 (Investment). The chart-placeholder zone introduced in M1 SHALL be removed and SHALL NOT render. Each group SHALL be headed by a title that states the question the group answers (产出 / 交付效率 / 质量 / 投入), so an operator can find the evidence behind a Signal Summary verdict by the dimension it concerns. The four dimension groups SHALL be fixed: the system SHALL render precisely these four and SHALL NOT add a fifth group or omit any of the four.

#### Scenario: The placeholder is replaced by four chart groups

- **WHEN** the Insights page is rendered for a project
- **THEN** below the Signal Summary the page SHALL render exactly four dimension groups named 产出, 交付效率, 质量, and 投入
- **AND** the chart-placeholder zone SHALL NOT render

#### Scenario: Each group carries a question-led title

- **WHEN** the four dimension groups are rendered
- **THEN** each group SHALL present a heading that names the dimension (产出 / 交付效率 / 质量 / 投入) so the group's question is identifiable
- **AND** the four groups SHALL appear in the fixed order 产出, 交付效率, 质量, 投入

#### Scenario: The dimension set is fixed

- **WHEN** the Insights charts section is rendered
- **THEN** the system SHALL render precisely the four dimension groups
- **AND** no fifth dimension group SHALL be introduced and no group SHALL be omitted

### Requirement: Each dimension group mounts the migrated charts unchanged in their internal behavior

Each migrated chart SHALL be relocated onto `/insights` within its assigned dimension group without altering its internal rendering, data-fetch, interaction, empty-state, or accessibility behavior relative to its Dashboard original. The groups and their chart membership SHALL be:

- 产出 (Output): `ThroughputChart`, `CompletionTrend`, `CumulativeFlowChart`
- 交付效率 (Delivery Efficiency): `CycleTimeChart`, `StageDurationChart`
- 质量 (Quality): `QualityPanel`, `FtrTrendChart`
- 投入 (Investment): `InvestmentPanel`, `CostTrendChart`

No new chart SHALL be introduced, and no migrated chart's internal logic SHALL be altered by the move.

#### Scenario: The 产出 group contains its three charts

- **WHEN** the 产出 (Output) group is rendered
- **THEN** it SHALL mount the `ThroughputChart`, `CompletionTrend`, and `CumulativeFlowChart` components

#### Scenario: The 交付效率 group contains its two charts

- **WHEN** the 交付效率 (Delivery Efficiency) group is rendered
- **THEN** it SHALL mount the `CycleTimeChart` and `StageDurationChart` components

#### Scenario: The 质量 group contains its two charts

- **WHEN** the 质量 (Quality) group is rendered
- **THEN** it SHALL mount the `QualityPanel` and `FtrTrendChart` components

#### Scenario: The 投入 group contains its two charts

- **WHEN** the 投入 (Investment) group is rendered
- **THEN** it SHALL mount the `InvestmentPanel` and `CostTrendChart` components

#### Scenario: Migrated charts keep their interaction, empty-state, and accessibility behavior

- **WHEN** a migrated chart renders on `/insights` in its assigned group
- **THEN** the chart SHALL preserve its interactions (e.g. lens toggles, overlays, expand/collapse), its empty states, and its accessibility behavior as they were on the Dashboard
- **AND** the relocation SHALL NOT alter the chart's internal rendering or data-fetch logic

### Requirement: EpicProgressList migrates to /insights without disturbing the four fixed groups

The `EpicProgressList` SHALL migrate to `/insights` alongside the other charts. It SHALL render either within the 产出 (Output) group or in its own standalone slot. Regardless of where `EpicProgressList` is placed, the four dimension groups' membership and order SHALL remain exactly as specified by the dimension-group requirement; the `EpicProgressList` placement decision SHALL NOT add, remove, or reorder the charts assigned to the four groups.

#### Scenario: EpicProgressList renders on Insights

- **WHEN** the Insights charts section is rendered
- **THEN** the `EpicProgressList` SHALL render on `/insights` within the charts area

#### Scenario: EpicProgressList placement does not alter the four groups

- **WHEN** `EpicProgressList` is placed in the 产出 group or in a standalone slot
- **THEN** the four dimension groups' chart membership and order SHALL remain unchanged
- **AND** the placement SHALL NOT introduce a fifth dimension group

### Requirement: Each migrated chart annotates the data time window its existing endpoint already yields

Each migrated chart SHALL display a time-window annotation that states the current data time window the chart's existing endpoint/hook already returns. The annotation SHALL be derived from the chart's existing fixed window — this change SHALL NOT implement a time-range selector (deferred to M3) and SHALL NOT change any chart's underlying window. The windows SHALL reflect: throughput 30d (daily completion), completion-trend 12w (weekly completion), delivery-time 30d, quality 7d + 30d windows plus the trend range, and for cumulative-flow, stage-duration, and cost-trend the server-provided `rangeFrom`/`rangeTo` (or equivalent) returned by each endpoint.

#### Scenario: Fixed-window charts show their known window

- **WHEN** a migrated chart with a fixed time window (e.g. throughput, completion-trend, delivery-time, quality) renders
- **THEN** the chart SHALL display an annotation stating its data time window (e.g. "30d", "12w", "7d", "90d") as derived from its existing endpoint

#### Scenario: Server-provided-range charts show the returned range

- **WHEN** a migrated chart whose endpoint returns a server-provided range (cumulative-flow, stage-duration, cost-trend) renders
- **THEN** the chart SHALL display a time-window annotation derived from the range its endpoint already returns
- **AND** the annotation SHALL NOT invent a window the endpoint does not return

#### Scenario: No time-range selector is introduced

- **WHEN** any migrated chart renders on `/insights`
- **THEN** the chart SHALL NOT expose a time-range selector control
- **AND** the chart's underlying data window SHALL remain its existing fixed/server-provided value

### Requirement: Migrated charts are reachable exclusively on /insights after migration

After migration, the trend charts SHALL be reachable exclusively on the `/insights` page. The migration SHALL NOT duplicate a chart onto the Dashboard, and SHALL NOT leave a migrated chart reachable only via the Dashboard. This makes `/insights` the single retrospective space for trending visualizations.

#### Scenario: Charts are present on Insights and not duplicated on the Dashboard

- **WHEN** the migration is complete
- **THEN** each migrated chart SHALL be reachable on `/insights` within its assigned dimension group
- **AND** the Dashboard SHALL NOT render any of the migrated trend charts
