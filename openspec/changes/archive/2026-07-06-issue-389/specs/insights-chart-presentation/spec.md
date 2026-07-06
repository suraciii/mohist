### Requirement: Every Retained Chart's Data Window Matches the Selected Range

Every chart retained on the Insights page MUST source its data from the page's selected time range (7d/30d/90d). No retained chart MUST keep a fixed window that ignores the selected range and creates a caliber exception.

#### Scenario: Switching the range updates every retained chart's window

- **WHEN** the user switches the page range between 7d, 30d, and 90d
- **THEN** every retained chart's underlying data request MUST be issued for the selected range
- **AND** no retained chart MUST continue to display data from a fixed window that does not correspond to the selection

### Requirement: Every Retained Chart Exposes a Verifiable Window Indicator

Every retained chart MUST expose a window indicator — a date-range label, a range code, or a caption — that lets a user verify the chart's data window corresponds to the selected range.

#### Scenario: Each chart carries a window indicator

- **WHEN** any retained chart is rendered with data
- **THEN** the chart MUST display a window indicator (date-range label, range code, or caption) reflecting the selected range

### Requirement: Retained Charts Enter Empty State on Zero or Sparse Samples

Every retained chart MUST enter its empty state when its sample population is zero or sparse, rather than rendering a precise value without sample-size context. A chart MUST NOT present a figure derived from too few samples as if it were a stable, precise measurement.

#### Scenario: Zero-sample window renders the empty state

- **WHEN** a retained chart's data source returns zero samples for the selected range
- **THEN** the chart MUST render its empty state
- **AND** MUST NOT render a precise value without sample-size context

#### Scenario: Existing empty-state handling is preserved

- **WHEN** the Stage Duration chart or the Throughput chart receives zero samples
- **THEN** the chart MUST continue to render its empty state as it did before this change
