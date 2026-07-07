### Requirement: Removed panels MUST NOT render on the Insights page

The `/insights` page MUST NOT render the Cumulative Flow chart, the Investment card, or the In-progress Epic progress list. Their empty-state placeholder copy and their identifying test surfaces (`productivity-investment`, `productivity-epic-list`, and any cumulative-flow chart container) MUST NOT appear in the rendered document, regardless of project state, data availability, or selected time range.

#### Scenario: Cumulative Flow chart is absent
- **WHEN** the `/insights` page is rendered for any project and any time range
- **THEN** no Cumulative Flow chart, no cumulative-flow empty-state placeholder, and no cumulative-flow chart container is present in the document

#### Scenario: Investment card is absent
- **WHEN** the `/insights` page is rendered for a project that has agent spend recorded
- **THEN** no Investment panel (no `productivity-investment` testid, no Expand/Collapse investment toggle, no cumulative-spend / cost-per-ship / shipped-issues rows) is present in the document

#### Scenario: In-progress Epic progress list is absent
- **WHEN** the `/insights` page is rendered for a project that has two or more active epics
- **THEN** no In-progress Epic progress list (no `productivity-epic-list` testid, no epic progress bars) is present in the document

### Requirement: Retained charts MUST be grouped into the four decision dimensions in fixed order

The `/insights` page MUST present its charts under exactly four dimension groups, rendered in this fixed order: 产出, 交付效率, 质量, 投入. Each group MUST contain exactly these charts and no others:

- 产出: Throughput, Completion Trend
- 交付效率: Cycle Time, Stage Duration
- 质量: AI Quality, First-Time-Right Trend
- 投入: Cost Trend

No panel or chart outside this list MAY render on `/insights`.

#### Scenario: Four groups render in the fixed order
- **WHEN** the `/insights` page is rendered
- **THEN** four chart groups appear in the order 产出, 交付效率, 质量, 投入

#### Scenario: Output group contains only Throughput and Completion Trend
- **WHEN** the 产出 group is inspected
- **THEN** it contains the Throughput chart and the Completion Trend chart, in that order, and no other panels

#### Scenario: Delivery-efficiency group contains Cycle Time and Stage Duration
- **WHEN** the 交付效率 group is inspected
- **THEN** it contains the Cycle Time chart and the Stage Duration chart, in that order, and no other panels

#### Scenario: Quality group contains AI Quality and First-Time-Right Trend
- **WHEN** the 质量 group is inspected
- **THEN** it contains the AI Quality panel and the First-Time-Right Trend chart, in that order, and no other panels

#### Scenario: Investment group contains only Cost Trend
- **WHEN** the 投入 group is inspected
- **THEN** it contains the Cost Trend chart and no other panels

### Requirement: Shared data hooks MUST remain available to their non-insights consumers

Removing the Investment card and the In-progress Epic progress list from `/insights` MUST NOT remove the `useCostRollup` or `useEpics` hooks, their backing HTTP endpoints, or their DTO types. These hooks MUST continue to serve their other consumers with unchanged behavior.

#### Scenario: useCostRollup still serves the dashboard headline
- **WHEN** the `FactoryStatusHeadline` widget on the dashboard renders
- **THEN** it consumes `useCostRollup` and displays the cost rollup as before, independent of the Investment panel's removal from `/insights`

#### Scenario: useEpics still serves the Epics list page
- **WHEN** the Epics list page renders
- **THEN** it consumes `useEpics` and renders epic progress grouped by status as before, independent of the In-progress Epic progress list's removal from `/insights`
