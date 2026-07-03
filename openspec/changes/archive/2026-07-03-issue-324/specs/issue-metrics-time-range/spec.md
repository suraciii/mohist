### Requirement: The issue metrics endpoints accept a uniform range query parameter

The six issue metrics endpoints — `completion`, `delivery-time`, `stage-duration`, `quality`, `cumulative-flow`, and `approval-wait` — SHALL accept a `range` query parameter whose accepted values are exactly `7d`, `30d`, and `90d`, giving the Insights selector a single uniform contract across the issue metrics read path.

#### Scenario: A valid range is accepted
- **WHEN** a request to any of the six issue metrics endpoints includes `range=30d`
- **THEN** the endpoint SHALL accept the request and compute its windows for a 30-day range

#### Scenario: An unknown range value is rejected
- **WHEN** a request to any of the six issue metrics endpoints includes a `range` value other than `7d`, `30d`, or `90d`
- **THEN** the endpoint SHALL return a 400 response

### Requirement: The range drives the current window length for every issue metrics endpoint

Each endpoint SHALL compute its current (trailing) window to span the number of calendar days implied by the selected range — 7 days for `7d`, 30 days for `30d`, 90 days for `90d` — regardless of its bucket granularity.

#### Scenario: The range scales the current window
- **WHEN** a request to the delivery-time endpoint specifies `range=90d`
- **THEN** the current window SHALL span 90 trailing days

### Requirement: The range drives the previous comparison window where one exists

Endpoints that derive a previous-adjacent comparison window — `completion`, `delivery-time`, and `quality` — SHALL scale the previous window to the same length as the current window implied by the range, immediately preceding the current window.

#### Scenario: The previous window matches the range length
- **WHEN** a request to the delivery-time endpoint specifies `range=7d`
- **THEN** the previous window SHALL span the 7 days immediately preceding the current 7-day window

### Requirement: Omitting the range reproduces today's exact fixed windows

When the `range` parameter is omitted, every endpoint SHALL reproduce the exact fixed window it serves today, so existing consumers (including the Dashboard widgets that share these hooks) are unaffected. Specifically: completion day bucket = 30 days, delivery-time = 30 days, stage-duration = 30 days, quality primary window = 30 days, cumulative-flow = 90 days, approval-wait = 7 days.

#### Scenario: An omitted range falls back to the fixed defaults
- **WHEN** a request to any issue metrics endpoint omits the `range` parameter
- **THEN** the endpoint SHALL return the same windows it serves today without the parameter

### Requirement: The cumulative-flow window is range-driven, superseding the fixed-window D6 contract

The cumulative-flow trailing window SHALL be derived from the selected range, formally superseding the prior design D6 contract (and the `dashboard-cumulative-flow` requirement) that the window be fixed and not user-configurable. The snapshot read path and the response DTO fields SHALL remain otherwise unchanged.

#### Scenario: The range controls the cumulative-flow window
- **WHEN** a request to the cumulative-flow endpoint specifies `range=30d`
- **THEN** the returned snapshot series SHALL span a 30-day trailing window

#### Scenario: An omitted range preserves the 90-day default
- **WHEN** a request omits the `range` parameter
- **THEN** the cumulative-flow window SHALL default to 90 days, preserving today's behavior

### Requirement: The quality double-window DTO keeps Window7d fixed while the range drives the primary window

The quality endpoint SHALL preserve the `Window7d` field as a fixed 7-day short-term lens regardless of the selected range. The selected range SHALL drive the primary window (the `Window30d` slot), its previous comparison window, and the trend span. No existing response field SHALL be removed.

#### Scenario: The range drives the primary quality window while Window7d stays fixed
- **WHEN** a request to the quality endpoint specifies `range=90d`
- **THEN** the primary window SHALL span 90 days
- **AND** the previous window SHALL span the prior 90 days
- **AND** the trend SHALL span 90 days
- **AND** the `Window7d` field SHALL still report a fixed 7-day window

#### Scenario: An omitted range preserves the 30-day primary window
- **WHEN** a request to the quality endpoint omits the `range` parameter
- **THEN** the primary window SHALL span 30 days and `Window7d` SHALL span 7 days, matching today's behavior
