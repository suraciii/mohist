## ADDED Requirements

### Requirement: Insights page is reachable via a project-scoped route and a sidebar navigation entry

The system SHALL expose a project-scoped `/insights` route that renders the Insights page, and SHALL add an "Insights" entry to the application sidebar so the page is reachable in the same navigation group as the other project pages. The route and the nav entry SHALL resolve to the currently selected project using the same project-scoping idiom the other project pages use; reaching Insights SHALL NOT require selecting a project beyond the existing project selection. The Insights page SHALL be a new page distinct from the Dashboard; it SHALL NOT alter, replace, or remove any existing Dashboard route or content.

#### Scenario: Sidebar exposes an Insights navigation entry

- **WHEN** the user views the application sidebar within a selected project
- **THEN** the sidebar SHALL render an "Insights" navigation entry
- **AND** activating it SHALL navigate to the project-scoped `/insights` route

#### Scenario: The /insights route renders the Insights page

- **WHEN** the user navigates to the `/insights` route for a selected project
- **THEN** the system SHALL render the Insights page
- **AND** the existing Dashboard route and its content SHALL remain unchanged and reachable

### Requirement: The Insights first screen is a conclusions-first Signal Summary over exactly four fixed dimensions

The Insights page's first screen SHALL be a **Signal Summary** that renders exactly four verdict sentences — 产出节奏 (throughput), 交付效率 (delivery), 质量信号 (quality), and 投入信号 (investment) — with the conclusion stated first, ahead of any supporting detail. The four dimensions SHALL be fixed: the system SHALL render precisely these four and SHALL NOT add a fifth verdict dimension or omit any of the four. The summary SHALL NOT migrate or embed any existing Dashboard chart in this milestone; below the summary the system SHALL render a chart-placeholder zone instead.

#### Scenario: The summary renders exactly four verdicts, conclusions first

- **WHEN** the Insights page is rendered for a project that has activity
- **THEN** the first screen SHALL present exactly four verdict sentences covering throughput, delivery, quality, and investment
- **AND** each verdict sentence SHALL state its conclusion before any supporting detail

#### Scenario: No fifth dimension is added and no dimension is omitted

- **WHEN** the Signal Summary is rendered
- **THEN** the summary SHALL contain precisely the throughput, delivery, quality, and investment verdicts
- **AND** no additional verdict dimension SHALL be introduced or removed

### Requirement: Each verdict carries current value, trend direction, and change magnitude derived strictly from the current-vs-previous window comparison

Each verdict SHALL carry the current-window value as its primary status, plus a trend direction (↑ / ↓ / 持平) and a change magnitude that are derived strictly from comparing the current window's value against the immediately preceding (previous adjacent) window's value returned by the corresponding metrics surface. The direction SHALL be ↑ when the current value is greater than the previous value, ↓ when it is less, and 持平 when the two are equal within a defined tolerance. The verdict SHALL NOT invent a trend from a single window, and SHALL NOT compare against a non-adjacent or cumulative baseline.

#### Scenario: Direction reflects the current-vs-previous numeric comparison

- **WHEN** a verdict's current-window value is greater than its previous-window value
- **THEN** the trend direction SHALL be ↑
- **AND** the change magnitude SHALL equal the difference derived from the two window values

#### Scenario: Equal current and previous values yield a flat direction

- **WHEN** a verdict's current-window value equals its previous-window value within the defined tolerance
- **THEN** the trend direction SHALL be 持平
- **AND** no misleading up or down arrow SHALL be rendered

#### Scenario: A trend is never derived from a single window alone

- **WHEN** a verdict's metrics surface returns a current window but no previous adjacent window
- **THEN** the verdict SHALL NOT render a trend direction or change magnitude
- **AND** the verdict SHALL degrade per the insufficient-data requirement

### Requirement: The throughput verdict compares completed-issue counts across the current and previous windows

The 产出节奏 verdict SHALL state the count of issues completed (reached `done`) in the current window and SHALL compare it against the count completed in the previous adjacent window, expressing the change as a count delta. An increase in completed count SHALL be conveyed as favorable. The verdict SHALL derive its counts from the completion-count surface's current-window and previous-window returns.

#### Scenario: Throughput verdict reports the completed-count delta

- **WHEN** the current window has 5 completed issues and the previous window had 3
- **THEN** the throughput verdict SHALL report the current count of 5
- **AND** the verdict SHALL convey an increase of 2 as a favorable trend

### Requirement: The delivery verdict compares cycle time across windows and names the slowest stage

The 交付效率 verdict SHALL state the average cycle time of issues delivered in the current window and SHALL compare it against the average cycle time of the previous adjacent window, expressing the change as a relative difference; a decrease in cycle time SHALL be conveyed as favorable (faster delivery). The verdict SHALL additionally name the slowest workflow stage — the stage with the greatest average stage duration — so the operator knows where delivery time is spent. When no stage-duration data is available, the slowest-stage name SHALL be omitted per the insufficient-data requirement rather than fabricated.

#### Scenario: Delivery verdict reports the cycle-time delta and names the slowest stage

- **WHEN** the current window's average cycle time is 5.2h, the previous window's is 6.3h, and the stage with the greatest average duration is "build"
- **THEN** the delivery verdict SHALL report the current 5.2h
- **AND** the verdict SHALL convey a faster trend with the relative decrease
- **AND** the verdict SHALL name the "build" stage as the slowest

#### Scenario: Slowest stage is omitted when stage-duration data is absent

- **WHEN** no stage-duration samples exist for the current window
- **THEN** the delivery verdict SHALL NOT name a slowest stage
- **AND** the verdict SHALL NOT fabricate a stage name

### Requirement: The quality verdict compares first-time-right rate across windows as a percentage-point delta

The 质量信号 verdict SHALL state the first-time-right rate of issues shipped in the current window and SHALL compare it against the first-time-right rate of the previous adjacent window, expressing the change in percentage points; an increase in first-time-right rate SHALL be conveyed as favorable. The verdict SHALL derive its rates from the quality surface's current-window and previous-window returns.

#### Scenario: Quality verdict reports the percentage-point delta

- **WHEN** the current window's first-time-right rate is 73% and the previous window's is 81%
- **THEN** the quality verdict SHALL report the current 73%
- **AND** the verdict SHALL convey a decrease of 8 percentage points as an unfavorable trend

### Requirement: The investment verdict compares spend and per-issue cost across windows

The 投入信号 verdict SHALL state the spend and the per-issue cost for the current window and SHALL compare each against the previous adjacent window's corresponding value; a decrease in spend or per-issue cost SHALL be conveyed as favorable. The verdict SHALL derive its figures from the agent-cost surface's current-window and previous-window returns.

#### Scenario: Investment verdict reports spend and per-issue-cost trends

- **WHEN** the current window's spend is $182 with 5 completed issues (per-issue $36) and the previous window's spend was $150 with 3 completed issues (per-issue $50)
- **THEN** the investment verdict SHALL report the current spend of $182 and the current per-issue cost of $36
- **AND** the verdict SHALL convey the spend trend and the per-issue-cost trend, each derived from the current-vs-previous comparison

### Requirement: Verdicts degrade gracefully when data is insufficient

When the data backing a verdict is insufficient — because the project is new, the current window has no samples, the previous adjacent window has no samples (no baseline), or the underlying metrics surface returns its defined empty result — the verdict SHALL degrade gracefully: it SHALL render the current value where one exists, SHALL hide the trend direction and change magnitude (or mark them "数据不足"), SHALL NOT render a misleading up or down arrow, and SHALL NOT raise an error. Each verdict SHALL be evaluated for insufficiency independently, so a verdict with sufficient data SHALL render normally alongside a verdict that degrades.

#### Scenario: Missing previous window hides the trend and avoids a misleading arrow

- **WHEN** a verdict has a current-window value but the previous adjacent window has no samples
- **THEN** the verdict SHALL render the current value
- **AND** the verdict SHALL NOT render a trend direction or change magnitude
- **AND** the verdict SHALL NOT render a misleading arrow

#### Scenario: No current-window samples marks the verdict as insufficient without erroring

- **WHEN** the current window has no samples for a given dimension
- **THEN** the verdict SHALL mark itself as data-insufficient (e.g. "数据不足")
- **AND** the page SHALL NOT raise an error or render a fabricated value

#### Scenario: Insufficiency is evaluated independently per verdict

- **WHEN** the throughput verdict has sufficient data but the quality verdict has no previous window
- **THEN** the throughput verdict SHALL render its full verdict
- **AND** the quality verdict SHALL degrade gracefully without affecting the throughput verdict

### Requirement: A chart-placeholder zone marks the future chart migration without rendering charts

Below the Signal Summary, the Insights page SHALL render a chart-placeholder zone that is visibly marked as a future deliverable (e.g. "图表将在后续迁移") and SHALL NOT render any migrated Dashboard chart in this milestone. The placeholder zone SHALL be present on the page so the later chart migration has a designated anchor point.

#### Scenario: The placeholder zone is present and marked for later migration

- **WHEN** the Insights page is rendered
- **THEN** a chart-placeholder zone SHALL appear below the Signal Summary
- **AND** the zone SHALL be marked as a future deliverable
- **AND** no migrated Dashboard chart SHALL be rendered in the zone

### Requirement: The Signal Summary is scoped to the project as a whole without epic or label drill-down

The Signal Summary SHALL evaluate every verdict against the selected project as a whole. The system SHALL NOT support narrowing a verdict to a single epic, a label, or any other sub-project slice in this milestone; the retrospective target SHALL be the entire project.

#### Scenario: Verdicts are computed at the project level

- **WHEN** the Signal Summary is rendered for a project
- **THEN** every verdict SHALL be computed over the project as a whole
- **AND** no epic or label drill-down control SHALL be offered
