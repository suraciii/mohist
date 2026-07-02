## ADDED Requirements

### Requirement: The AI-quality surface returns a previous-adjacent-window first-time-right rate alongside the current window

The AI-quality surface SHALL additionally return the **first-time-right rate** computed over the immediately preceding window of the same length as the current first-time-right window — the window adjacent to and immediately before the current window — using the identical first-time-right classification (a shipped issue is first-time-right if and only if no check across its whole lifecycle triggered a repair) and the identical ship-time windowing the existing single-point first-time-right aggregation already uses. The surface SHALL return the current-window first-time-right rate alongside this previous-window rate so a consumer can derive the percentage-point delta and direction in a single read. When either window contains no shipped issues, that window's rate SHALL be the defined empty result distinguishable from a genuine rate, evaluated independently per window. This return SHALL be strictly additive: the existing 7-day and 30-day single-point first-time-right rates, the per-stage rework rates, the per-bucket trend series, and the existing zero-sample empty-result semantics SHALL be preserved unchanged; only the previous-window first-time-right rate is added.

#### Scenario: The previous window is the same length as and immediately precedes the current window

- **WHEN** the AI-quality surface is requested with the current first-time-right window `[now - W, now]`
- **THEN** the surface SHALL also return the previous window `[now - 2W, now - W]`
- **AND** the previous-window first-time-right rate SHALL be computed using the identical ship-time windowing and first-time-right classification as the current window

#### Scenario: Both windows' rates are returned for percentage-point delta derivation

- **WHEN** the current window's first-time-right rate is 0.73 and the previous window's is 0.81
- **THEN** the surface SHALL return both rates
- **AND** a consumer SHALL be able to derive an 8-percentage-point decrease between the two windows

#### Scenario: A window with no shipped issues yields the empty result, independent of the other window

- **WHEN** the current window contains shipped issues but the previous window contains none
- **THEN** the previous-window first-time-right rate SHALL be the defined empty result
- **AND** the current-window rate SHALL still report its computed value
- **AND** the empty previous-window result SHALL be distinguishable from a genuine rate of one or zero

#### Scenario: The existing single-point aggregation and trend series are preserved

- **WHEN** the previous-window first-time-right rate is added to the surface
- **THEN** the existing 7-day and 30-day single-point first-time-right rates, the per-stage rework rates, and the per-bucket trend series SHALL remain available and unchanged
- **AND** the previous-window rate SHALL be strictly additive to the existing response
