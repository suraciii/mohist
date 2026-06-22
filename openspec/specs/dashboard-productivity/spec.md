# OpenSpec Capability: dashboard-productivity

### Requirement: Productivity zone mounts in the Dashboard productivity slot

The Productivity zone SHALL mount into the Dashboard `productivity` slot established by the `dashboard-shell` capability, replacing the generic empty placeholder that previously occupied the slot.

#### Scenario: Productivity zone replaces the empty placeholder

- **WHEN** the Dashboard page renders for a project that has at least one project
- **THEN** the `productivity` slot SHALL render the Productivity zone component
- **AND** the slot SHALL NOT render the generic empty `DashboardZonePlaceholder`

### Requirement: Productivity zone composes existing data sources without new queries

The Productivity zone SHALL source all of its data exclusively from (a) the Issue completion snapshot and time-series aggregation provided by the `issue-completion-metrics` capability, (b) the Agent/Session usage snapshot and time-series provided by the Agent/Session usage metrics, and (c) the existing `useEpics()` Epic progress query. The Productivity zone SHALL NOT introduce any new server endpoint, HTTP route, persistence field, or domain query.

#### Scenario: No new backend query is introduced

- **WHEN** the Productivity zone renders and displays snapshot, trend, Epic progress, and investment data
- **THEN** every displayed figure SHALL be sourced from the completion snapshot, the completion time-series endpoint, the Agent/Session usage snapshot/time-series, or `useEpics()`
- **AND** the Productivity zone SHALL NOT call any endpoint or domain query created by this issue

### Requirement: Productivity zone renders the weekly completion snapshot

The Productivity zone SHALL display a snapshot row showing this week's `completed`, `failed`, and `new` counts, sourced from the completion snapshot provided by the `issue-completion-metrics` capability (e.g. `useCompletionSnapshot`).

#### Scenario: Snapshot counts are displayed

- **WHEN** the Productivity zone renders and the completion snapshot returns `completed: 5`, `failed: 1`, `new: 8`
- **THEN** the zone SHALL display the three counts `5`, `1`, and `8`
- **AND** each count SHALL be labeled as completed, failed, and new respectively

#### Scenario: Zero counts render as zero rather than hidden

- **WHEN** the completion snapshot returns all-zero counts for the week
- **THEN** the zone SHALL render the zeros visibly in the snapshot row
- **AND** the zone SHALL NOT hide or remove the snapshot row

### Requirement: Productivity zone renders in-progress Epic progress bars

The Productivity zone SHALL render progress bars for in-progress Epics sourced from `useEpics()`, where each bar fills proportionally to `progress.deliveredCount / progress.totalIssueCount`. The zone SHALL render at least two in-progress Epics when that many are available, and SHALL render an empty state when fewer than two in-progress Epics exist.

#### Scenario: Two or more in-progress Epics render progress bars

- **WHEN** `useEpics()` returns three Epics with status `active` and non-zero `totalIssueCount`
- **THEN** the zone SHALL render a progress bar for at least two of them
- **AND** each bar SHALL fill proportionally to `deliveredCount / totalIssueCount`

#### Scenario: Fewer than two in-progress Epics shows empty state

- **WHEN** `useEpics()` returns zero or one in-progress Epic
- **THEN** the zone SHALL render an Epic progress empty state
- **AND** the zone SHALL NOT render a partial single-bar layout as if it were the intended full view

#### Scenario: Epic with no issues does not divide by zero

- **WHEN** an in-progress Epic has `totalIssueCount: 0`
- **THEN** its progress bar SHALL render as empty (zero fill) without error
- **AND** the zone SHALL NOT attempt division by zero when computing the fill ratio

### Requirement: Productivity zone renders the completion trend

The Productivity zone SHALL render a completion-count trend visualization sourced from the Issue-context time-series aggregation endpoint provided by the `issue-completion-metrics` capability, using by-week buckets. The visualization SHALL be rendered with lightweight inline SVG or existing UI primitives and SHALL NOT introduce a third-party charting library. The trend SHALL consume the fixed bucketing provided by the endpoint and SHALL NOT offer a configurable time range.

#### Scenario: Trend renders weekly completion buckets

- **WHEN** the time-series endpoint returns by-week buckets with terminal-state completion counts
- **THEN** the zone SHALL render a trend visualization whose points correspond to the weekly buckets
- **AND** the trend SHALL reflect completion counts changing over time

#### Scenario: No charting library is added

- **WHEN** the trend visualization is implemented
- **THEN** it SHALL be rendered with inline SVG or existing UI primitives
- **AND** no new charting dependency SHALL be added to the web package

#### Scenario: Trend does not offer a configurable time range

- **WHEN** the Productivity zone renders the trend
- **THEN** it SHALL use the fixed by-week bucketing supplied by the endpoint
- **AND** it SHALL NOT expose a user-facing control for custom bucket size or custom time range

### Requirement: Investment section is collapsed by default with caliber annotation

The Productivity zone SHALL render an "investment" (投入) section sourced from the Agent/Session usage metrics. The section SHALL be collapsed by default on first render. When expanded, the section SHALL annotate the caliber/basis of the displayed figures (for example, the token or cost aggregation window, or the population the figures cover) so the numbers are not misread.

#### Scenario: Investment is collapsed on first render

- **WHEN** the Productivity zone renders for the first time
- **THEN** the investment section SHALL be collapsed
- **AND** its detailed figures SHALL NOT be visible without an explicit user expand action

#### Scenario: Expanding investment reveals caliber annotation

- **WHEN** a user expands the investment section
- **THEN** the section SHALL display the usage figures alongside an annotation of their caliber/basis
- **AND** the annotation SHALL identify what window or population the figures cover

### Requirement: Productivity zone provides per-section empty states

When any individual data source has no data, the Productivity zone SHALL render a meaningful empty state for the affected section while continuing to render the other sections normally. The zone SHALL NOT render a broken or fully blank panel when at least one section has data, and SHALL degrade gracefully when all sources are empty.

#### Scenario: All data sources empty

- **WHEN** the project has no issues, no in-progress Epics, and no Agent/Session usage data
- **THEN** the Productivity zone SHALL render empty states for the snapshot, Epic progress, trend, and investment sections
- **AND** the zone SHALL NOT render a broken or blank panel

#### Scenario: Partial data renders available sections alongside empty states

- **WHEN** completion data exists but no in-progress Epics exist
- **THEN** the zone SHALL render the snapshot and trend sections with their data
- **AND** the zone SHALL render the Epic progress empty state alongside them