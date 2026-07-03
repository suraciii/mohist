### Requirement: The agent cost endpoint accepts a range parameter that re-bases the windowed spend figures

The `/agent/cost` endpoint SHALL accept a `range` query parameter whose accepted values are exactly `7d`, `30d`, and `90d`. The windowed current and previous spend and per-issue-cost figures SHALL be computed over windows derived from the selected range. The all-time `totalCost`, `todayCost`, and all-time `costPerShip` SHALL NOT be affected by the range.

#### Scenario: The range scales the windowed spend
- **WHEN** a request to `/agent/cost` specifies `range=90d`
- **THEN** the current window spend SHALL span 90 days
- **AND** the previous window SHALL span the prior 90 days

#### Scenario: All-time figures are unaffected by the range
- **WHEN** a request to `/agent/cost` specifies any range
- **THEN** `totalCost`, `todayCost`, and `costPerShip` SHALL be identical to the values returned without a range

#### Scenario: An unknown range value is rejected
- **WHEN** a request to `/agent/cost` includes a `range` value other than `7d`, `30d`, or `90d`
- **THEN** the endpoint SHALL return a 400 response

### Requirement: The agent usage endpoint accepts a range parameter that re-bases the timeseries

The `/agent/usage` endpoint SHALL accept a `range` query parameter whose accepted values are exactly `7d`, `30d`, and `90d`. The usage timeseries span and its buckets SHALL be computed over the window implied by the selected range.

#### Scenario: The range scales the usage timeseries span
- **WHEN** a request to `/agent/usage` specifies `range=30d`
- **THEN** the returned timeseries SHALL span a 30-day window

#### Scenario: An unknown range value is rejected
- **WHEN** a request to `/agent/usage` includes a `range` value other than `7d`, `30d`, or `90d`
- **THEN** the endpoint SHALL return a 400 response

### Requirement: The usage bucket granularity adapts to the selected range

The usage timeseries bucket granularity SHALL adapt to the selected range so the series remains legible (e.g. daily buckets for shorter ranges, coarser buckets for `90d`). The exact granularity-to-range mapping SHALL be documented in `design.md`.

#### Scenario: The granularity adapts for a 90-day range
- **WHEN** a request to `/agent/usage` specifies `range=90d`
- **THEN** the returned `bucketGranularity` SHALL reflect a granularity appropriate to a 90-day span
- **AND** the chosen mapping SHALL be recorded in design.md

### Requirement: Omitting the range reproduces today's fixed windows for agent cost and usage

When the `range` parameter is omitted, `/agent/cost` SHALL reproduce today's fixed 30-day windowed figures and `/agent/usage` SHALL reproduce today's fixed 7-day, 7-bucket daily timeseries, so existing consumers (including the Dashboard `FactoryStatusHeadline`) are unaffected.

#### Scenario: An omitted range falls back to the fixed cost window
- **WHEN** a request to `/agent/cost` omits the `range` parameter
- **THEN** the windowed figures SHALL span 30 days, matching today's behavior

#### Scenario: An omitted range falls back to the fixed usage window
- **WHEN** a request to `/agent/usage` omits the `range` parameter
- **THEN** the timeseries SHALL span 7 days with 7 daily buckets, matching today's behavior
