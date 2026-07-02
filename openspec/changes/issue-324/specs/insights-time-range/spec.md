### Requirement: The Insights page renders a global time-range selector with exactly three presets

The `/insights` page SHALL render a single global time-range selector control offering exactly three presets: `7d`, `30d`, and `90d`. The selector SHALL NOT offer a custom from/to date picker. The selector SHALL be the one control that re-bases the Signal Summary and every chart in a single action.

#### Scenario: Selector offers exactly three presets
- **WHEN** the Insights page renders
- **THEN** a time-range selector SHALL be present offering exactly `7d`, `30d`, and `90d`
- **AND** no custom from/to date picker SHALL be offered

#### Scenario: The selector is the single control for the whole page
- **WHEN** the operator changes the selector
- **THEN** the selected range SHALL be applied to the Signal Summary and every chart simultaneously

### Requirement: The default selected range is 30d

On initial load, the selector SHALL default to `30d`, the window closest to today's predominant metric windows, so the page opens on a familiar baseline.

#### Scenario: Initial selection
- **WHEN** the Insights page loads for the first time
- **THEN** the selector SHALL show `30d` as the active selection

### Requirement: InsightsPage owns the selected range as page-level state and propagates it downstream

`InsightsPage` SHALL hold the selected range in page-level state and propagate it to `SignalSummary` and to `InsightsCharts`, which forwards it to every chart panel and its data hook. The range SHALL flow from this single page-level source and SHALL NOT be independently fetched or derived per chart.

#### Scenario: Range flows from the page to every chart
- **WHEN** InsightsPage holds a selected range
- **THEN** SignalSummary and every chart panel SHALL receive that same range value

### Requirement: Each of the eight Insights data hooks threads the range into its request

The eight Insights data hooks — `useCompletionThroughput`, `useCompletionTrend`, `useCumulativeFlow`, `useDeliveryTime`, `useStageDuration`, `useQualityMetrics`, `useAgentUsage`, and `useCostRollup` — SHALL accept the selected range and append it as a query parameter to their fetch URL.

#### Scenario: A hook sends the range to the server
- **WHEN** a chart hook is invoked with a selected range
- **THEN** the hook SHALL include the range in its request to the corresponding metrics endpoint

### Requirement: The range is folded into each hook's queryKey to prevent stale cross-range cache

Each of the eight hooks SHALL include the selected range in its `queryKey` so that switching the range fetches fresh data and never serves a cached response computed for a different range.

#### Scenario: Switching range does not serve another range's cache
- **WHEN** the selector switches from one range to another
- **THEN** each hook's queryKey SHALL differ by the range value
- **AND** the hook SHALL issue a fresh request for the new range rather than returning cached data from the prior range

### Requirement: Switching the range re-bases the Signal Summary and every chart

When the operator switches the range, the Signal Summary verdicts and every chart SHALL refresh to reflect the selected window once fresh data arrives.

#### Scenario: Signal Summary verdicts follow the range
- **WHEN** the selector switches to a new range and the data loads
- **THEN** the Signal Summary verdicts SHALL reflect metrics computed over the new range

#### Scenario: Every chart follows the range
- **WHEN** the selector switches to a new range and the data loads
- **THEN** every chart SHALL render data computed over the new range

### Requirement: EpicProgressList is exempt from the range selector

`EpicProgressList` — a live in-progress epic snapshot backed by `useEpics` — SHALL NOT consume the selected range, because it carries no time window.

#### Scenario: The epic list ignores the selector
- **WHEN** the selector switches to a new range
- **THEN** EpicProgressList SHALL remain unchanged by the range

### Requirement: Per-chart window badges reflect the selected range

Every chart window badge SHALL reflect the window implied by the selected range (or the actual `rangeFrom`/`rangeTo` returned by the server), so no badge displays a stale hard-coded window after a range switch.

#### Scenario: Badges follow the selected range
- **WHEN** the selector switches to a new range and the data loads
- **THEN** each chart's window badge SHALL display the window for the new range

### Requirement: Non-Insights consumers of the shared hooks are unaffected

The eight hooks SHALL accept the range as an optional argument so that non-Insights consumers (e.g. the Dashboard `FactoryStatusHeadline` consuming `useCostRollup`) that do not pass a range continue to operate against the server's back-compat default windows.

#### Scenario: A non-Insights consumer calls a shared hook without a range
- **WHEN** a non-Insights consumer invokes a shared hook without a range argument
- **THEN** the hook SHALL request data without the range parameter
- **AND** the response SHALL reflect the server's back-compat default window
