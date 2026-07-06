### Requirement: Insights Page Renders No Signal Summary Block

The `/insights` page MUST NOT render a "Signal Summary" block. The page-level time-range selector MUST be followed directly by the chart region, with no intermediate textual summary section, no verdict cards, and no conclusion-first subtitle framing.

#### Scenario: Charts follow the range selector with no summary in between

- **WHEN** a user opens the `/insights` page
- **THEN** the page renders the time-range selector
- **AND** the chart region is rendered directly beneath the time-range selector
- **AND** no Signal Summary element (the `signal-summary` test id or a "Signal Summary" heading) is present in the DOM

#### Scenario: Page subtitle does not frame the page as conclusion-first

- **WHEN** a user opens the `/insights` page
- **THEN** the page subtitle MUST NOT instruct the user to read a conclusion before the charts
- **AND** the subtitle MUST NOT reference a Signal Summary block

### Requirement: Signal Summary Model Layer and UI Component Removed

The Insights page MUST NOT depend on a verdict-derivation model layer — no signal-summary composer, no per-dimension verdict derivations, and no `Verdict` union — to produce textual summaries. The `SignalSummary` UI component MUST NOT be rendered on the page.

#### Scenario: Page module does not consume the verdict layer

- **WHEN** the Insights page module is inspected
- **THEN** it MUST NOT import or invoke a signal-summary composer or any per-dimension verdict derivation
- **AND** it MUST NOT reference a `SignalSummary` UI component

### Requirement: Page-Level Metrics Hooks Remain Available to Charts

The data-fetch hook functions that the Insights charts consume (`useCompletionThroughput`, `useDeliveryTime`, `useQualityMetrics`, `useCostRollup`, `useStageDuration`) MUST remain available and MUST continue to supply the charts with range-driven data. Only the Signal Summary is removed from the set of consumers of these hooks; no chart MUST lose its data source as a side effect of removing the Signal Summary.

#### Scenario: Charts continue to receive range-driven data after Signal Summary removal

- **WHEN** a user selects a range on the `/insights` page
- **THEN** each chart that depends on a page-level metrics hook MUST continue to render using data fetched for the selected range
- **AND** the chart's data MUST reflect the selected range, not a fixed window
