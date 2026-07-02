## ADDED Requirements

### Requirement: The delivery-time surface returns a previous-adjacent-window average cycle time alongside the current window

The delivery-time surface SHALL additionally return the **average cycle time** computed over the immediately preceding window of the same length as the existing fixed trailing window — i.e. the window `[now - 2W, now - W]` adjacent to the current window `[now - W, now]` — using the identical cycle-time definition (earliest work-start to final completion, surviving retries) and the identical completion-time windowing the current window already uses. The surface SHALL return the current-window average cycle time alongside this previous-window average so a consumer can derive the cycle-time delta and direction in a single read. When either window contains no delivered issues, that window's average SHALL be the defined empty result distinguishable from a genuine computed duration, evaluated independently per window. This return SHALL be strictly additive: the existing per-issue delivery-time series, the existing fixed trailing window, and the existing empty-result semantics SHALL be preserved unchanged; only the previous-window average is added.

#### Scenario: The previous window is the same length as and immediately precedes the current window

- **WHEN** the delivery-time surface is requested with the current window `[now - W, now]`
- **THEN** the surface SHALL also return the previous window `[now - 2W, now - W]`
- **AND** the previous-window average cycle time SHALL be computed using the same earliest-work-start-to-final-completion definition as the current window

#### Scenario: Both windows' averages are returned for delta derivation

- **WHEN** the current window's average cycle time is 5.2 days and the previous window's is 6.3 days
- **THEN** the surface SHALL return both averages
- **AND** a consumer SHALL be able to derive that cycle time decreased by the difference between the two windows

#### Scenario: A window with no delivered issues yields the empty result, independent of the other window

- **WHEN** the current window contains delivered issues but the previous window contains none
- **THEN** the previous-window average cycle time SHALL be the defined empty result
- **AND** the current-window average SHALL still report its computed value
- **AND** the empty previous-window result SHALL be distinguishable from a genuine zero-duration average

#### Scenario: The existing per-issue series and fixed window are preserved

- **WHEN** the previous-window average is added to the surface
- **THEN** the existing per-issue delivery-time series and the existing fixed trailing window SHALL remain available and unchanged
- **AND** the previous-window average SHALL be strictly additive to the existing response
