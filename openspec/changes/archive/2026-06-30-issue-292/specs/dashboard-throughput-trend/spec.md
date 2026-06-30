## ADDED Requirements

### Requirement: Daily throughput bar chart renders one bar per trailing day in the Productivity zone

The Dashboard `Productivity` zone SHALL render a daily delivery throughput bar chart: one bar per trailing day across a fixed trailing 30-day window, with each bar's primary height encoding that day's completed-issue count. Each bar SHALL overlay a darker failed segment on the same shared issue-count axis, and that failed segment SHALL encode that day's failed terminations (issues that entered `cancelled`) without capping to the completed count. The per-bucket completed and failed counts SHALL be sourced from the existing completion metrics endpoint at daily bucket resolution (`GET /api/projects/{ref}/issues/metrics/completion?bucket=day`); the throughput widget SHALL NOT introduce a second data collection path. The 30-day window SHALL be fixed and SHALL NOT be user-configurable.

#### Scenario: One bar renders per trailing day, height from that day's completed count

- **WHEN** the Productivity zone renders the daily throughput bar chart for a project whose completion metrics have completed issues across the trailing 30-day window
- **THEN** the chart SHALL render one bar per trailing day across the fixed 30-day window
- **AND** each bar's height SHALL encode that day's completed-issue count sourced from the completion metrics daily bucket

#### Scenario: A failed segment overlays each day's bar on the same count axis

- **WHEN** a day within the trailing window has one or more failed terminations (issues that entered `cancelled`)
- **THEN** the chart SHALL render a darker failed segment overlaid on that day's bar using the same count axis
- **AND** the failed segment SHALL encode that day's failed count sourced from the same completion metrics daily bucket's `Failed` value
- **AND** the failed segment SHALL NOT be capped to the completed count when failures exceed completions

#### Scenario: A day with no completions renders a zero-height bar rather than a gap

- **WHEN** a day within the trailing window has no completed and no failed issues
- **THEN** the chart SHALL render a bar for that day at zero height
- **AND** the chart SHALL NOT omit the day or collapse the axis

#### Scenario: Bar values come from the existing completion metrics endpoint, not a new collection

- **WHEN** the daily throughput bar chart renders
- **THEN** the per-day completed and failed values SHALL be sourced from the existing completion metrics endpoint at daily bucket resolution
- **AND** the widget SHALL NOT introduce a new data collection path for daily throughput

#### Scenario: The trailing window is fixed at 30 days and is not configurable

- **WHEN** the daily throughput bar chart renders
- **THEN** the trailing window SHALL span a fixed 30 days
- **AND** the window length SHALL NOT be configurable by the user

### Requirement: A 7-day moving average line overlays the completed series

The daily throughput bar chart SHALL overlay a 7-day moving average line across the completed series, smoothing single-day spikes and revealing the underlying delivery trend. The moving average SHALL be computed over the per-day completed counts; each plotted point SHALL be the average of the completed counts of the day it is plotted on and the six preceding days in the window. The moving average SHALL be derived client-side from the completed series returned by the completion metrics endpoint; the widget SHALL NOT require a new backend computation.

#### Scenario: Moving average plots the 7-day mean of completed counts

- **WHEN** the daily throughput bar chart renders the moving average overlay for a window in which a day and the six preceding days have completed counts
- **THEN** the line SHALL plot, for that day, the mean of the completed counts of that day and the six preceding days
- **AND** the moving average SHALL be derived client-side from the completed series

#### Scenario: Days near the start of the window with fewer than seven predecessors still plot

- **WHEN** a day in the window has fewer than six preceding days available (near the start of the trailing window)
- **THEN** the line SHALL plot the moving average over the available preceding days plus the day itself
- **AND** the line SHALL NOT be omitted for that day solely because fewer than seven samples exist

#### Scenario: The moving average reflects the completed series, not the failed segment

- **WHEN** the moving average overlay renders alongside bars that carry stacked failed segments
- **THEN** the moving average SHALL be computed over the completed counts only
- **AND** failed terminations SHALL NOT be folded into the moving average values

### Requirement: Throughput widget renders loading, error, and empty states with a next action

The daily throughput widget SHALL render the loading state while the underlying completion metrics data is in flight, the error state when the underlying data fetch fails, and the empty state when the project has no completed issues yet. The empty state SHALL name a concrete next action telling the operator how the chart will gain data — that throughput appears once an issue completes on the project. The widget SHALL route these states through the shared dashboard chart three-state wrapper, and SHALL NOT render a bare empty axis.

#### Scenario: Loading state renders while data is in flight

- **WHEN** the underlying completion metrics data for the throughput chart is still loading
- **THEN** the widget SHALL render the loading state via the shared chart three-state wrapper
- **AND** the chart content SHALL NOT render until data has resolved

#### Scenario: Error state renders when the data fetch fails

- **WHEN** the underlying completion metrics fetch for the throughput chart fails
- **THEN** the widget SHALL render the error state via the shared chart three-state wrapper
- **AND** the widget SHALL NOT render stale or fabricated chart content

#### Scenario: Empty state renders with a next action and no bare axis

- **WHEN** the project has no completed issues recorded yet
- **THEN** the widget SHALL render the empty state via the shared chart three-state wrapper
- **AND** the empty state SHALL name a concrete next action describing that the chart gains data once an issue completes on the project
- **AND** the widget SHALL NOT render a bare empty coordinate axis

### Requirement: Throughput bars are bucketed by completion-event time, not by issue edit time

The throughput chart's per-day values SHALL be driven by the completion-event timestamps (`IssueWorkCompleted` for `done`, `IssueClosed` for `cancelled`) that the completion metrics endpoint already buckets on. A post-completion edit to an issue (comment, label, title, or any update that bumps `updatedAt` after the issue has already terminated) SHALL NOT move, add, or resurface a bar, because bars are sourced from terminal-event time rather than from the issue's last-update time.

#### Scenario: A post-completion edit does not move the bar

- **WHEN** an issue completed on a prior day is edited on the current day in a way that bumps its `updatedAt`
- **THEN** the chart SHALL NOT move, add, or resurface a bar for the current day as a result of that edit
- **AND** the issue's completion SHALL continue to be counted in the bucket for the day of its terminal event

#### Scenario: Reopens and re-completions count at the latest terminal moment

- **WHEN** an issue is reopened and later re-enters a terminal state on a new day
- **THEN** the chart SHALL count the completion in the bucket for the day of the new terminal event
- **AND** the chart SHALL NOT also retain the completion in the prior terminal day's bucket

### Requirement: Throughput widget composes against the dashboard chart baseline and is read-only

The daily throughput widget SHALL compose against the reusable dashboard chart baseline: the single pinned chart library, the theme-token color contract, the shared three-state wrapper, the accessibility wrapper (a screen-reader data summary and a legend that does not rely on color alone to distinguish the completed, failed, and moving-average series), and the numeric (`tabular-nums`) and motion (`transform`-based, `prefers-reduced-motion`-aware) conventions. The widget SHALL NOT introduce a new charting library dependency. The widget SHALL be purely read-only: it SHALL NOT mutate issue, session, or workflow state, and SHALL NOT introduce any new backend write, event, or data collection beyond the additive daily-resolution read of the existing completion metrics endpoint.

#### Scenario: Widget uses the chart baseline components and conventions

- **WHEN** the daily throughput widget renders its chart
- **THEN** the widget SHALL render through the single pinned chart library
- **AND** the widget SHALL source colors from theme tokens, route states through the shared chart three-state wrapper, expose the accessibility wrapper, and apply the tabular-nums and transform-based motion conventions
- **AND** the widget SHALL NOT introduce a new charting library dependency

#### Scenario: Legend distinguishes completed, failed, and moving average without color alone

- **WHEN** the throughput chart renders its legend
- **THEN** the legend SHALL disambiguate the completed series, the failed segment, and the moving-average line by a channel other than color (label, shape, or pattern)
- **AND** a user who cannot perceive color SHALL be able to tell the series apart from the legend

#### Scenario: Widget is read-only with respect to domain state

- **WHEN** a user views or refreshes the daily throughput widget
- **THEN** the widget SHALL NOT perform any write or mutation against issue, session, or workflow domain state
- **AND** the widget SHALL NOT introduce any new backend write, event, or data collection beyond the additive daily-resolution read of the existing completion metrics endpoint
