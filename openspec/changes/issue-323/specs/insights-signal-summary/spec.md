### Requirement: The Insights Signal Summary renders exactly four fixed-dimension verdicts with conclusions first

The Insights page's first screen SHALL be a **Signal Summary** that renders exactly four verdict sentences — 产出节奏 (throughput), 交付效率 (delivery), 质量信号 (quality), and 投入信号 (investment) — with the conclusion stated first, ahead of any supporting detail. The four dimensions SHALL be fixed: the system SHALL render precisely these four and SHALL NOT add a fifth verdict dimension or omit any of the four.

#### Scenario: The summary renders exactly four verdicts, conclusions first

- **WHEN** the Insights page is rendered for a project that has activity
- **THEN** the first screen SHALL present exactly four verdict sentences covering throughput, delivery, quality, and investment
- **AND** each verdict sentence SHALL state its conclusion before any supporting detail

#### Scenario: The dimension set is fixed

- **WHEN** the Signal Summary is rendered
- **THEN** the summary SHALL contain precisely the throughput, delivery, quality, and investment verdicts
- **AND** no additional verdict dimension SHALL be introduced or removed

### Requirement: Each verdict carries current value, trend direction, and change magnitude derived strictly from the current-vs-previous window comparison

Each verdict SHALL carry the current-window value as its primary status, plus a trend direction (↑ / ↓ / 持平) and a change magnitude derived strictly from comparing the current window's value against the immediately preceding (previous adjacent) window's value returned by the corresponding metrics surface. The direction SHALL be ↑ when the current value is greater than the previous value, ↓ when it is less, and 持平 when the two are equal within a defined tolerance. The verdict SHALL NOT invent a trend from a single window, and SHALL NOT compare against a non-adjacent or cumulative baseline.

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

### Requirement: Verdicts degrade gracefully when data is insufficient

When the data backing a verdict is insufficient — because the project is new, the current window has no samples, the previous adjacent window has no samples (no baseline), or the underlying metrics surface returns its defined empty result — the verdict SHALL degrade gracefully: it SHALL render the current value where one exists, SHALL hide the trend direction and change magnitude (or mark them "数据不足"), SHALL NOT render a misleading up or down arrow, and SHALL NOT raise an error. Each verdict SHALL be evaluated for insufficiency independently.

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

### Requirement: The Signal Summary is scoped to the project as a whole without epic or label drill-down

The Signal Summary SHALL evaluate every verdict against the selected project as a whole. The system SHALL NOT support narrowing a verdict to a single epic, a label, or any other sub-project slice; the retrospective target SHALL be the entire project.

#### Scenario: Verdicts are computed at the project level

- **WHEN** the Signal Summary is rendered for a project
- **THEN** every verdict SHALL be computed over the project as a whole
- **AND** no epic or label drill-down control SHALL be offered

### Requirement: The area below the Signal Summary renders real migrated charts, not a placeholder

Below the Signal Summary, the Insights page SHALL render the migrated trend charts (organized into dimension groups) and SHALL NOT render the chart-placeholder zone. The placeholder contract established in the previous milestone is superseded: the page SHALL present actual charts rather than a future-deliverable marker, and SHALL NOT render a placeholder zone marked as a future deliverable.

#### Scenario: No chart-placeholder zone remains

- **WHEN** the Insights page is rendered
- **THEN** the page SHALL NOT render a chart-placeholder zone below the Signal Summary
- **AND** the area below the Signal Summary SHALL contain the real migrated charts

#### Scenario: The Signal Summary itself remains the conclusions-first first screen

- **WHEN** the Insights page is rendered
- **THEN** the Signal Summary SHALL remain the first screen and SHALL present its verdict conclusions before the migrated charts
- **AND** the verdict sentences' content and derivation SHALL be unchanged by the chart migration
