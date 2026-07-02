## ADDED Requirements

### Requirement: Completion counts are classified from terminal issue transition events

The completion-count surface SHALL classify each project issue's terminal outcome from its durable lifecycle transition events: an issue whose latest terminal event is the work-completed event SHALL count as **completed** (reached `done`); an issue whose latest terminal event is the issue-closed event SHALL count as **failed** (cancelled). An issue SHALL be counted by its latest terminal event only, so that an issue reopened and re-completed contributes to the bucket of its final terminal event and does not leave a stale count in an earlier bucket. Issues that have not reached a terminal state SHALL NOT contribute to any completion count.

#### Scenario: A completed issue counts as completed

- **WHEN** an issue's latest terminal event is the work-completed event
- **THEN** the issue SHALL count as completed
- **AND** the issue SHALL contribute to the completed count of the bucket holding that event's time

#### Scenario: A cancelled issue counts as failed

- **WHEN** an issue's latest terminal event is the issue-closed event
- **THEN** the issue SHALL count as failed
- **AND** the issue SHALL contribute to the failed count of the bucket holding that event's time

#### Scenario: A reopened and re-completed issue is counted once at its final outcome

- **WHEN** an issue was completed in one bucket and later reopened and completed again in a later bucket
- **THEN** the issue SHALL count exactly once, in the later bucket of its final terminal event
- **AND** the earlier bucket SHALL NOT retain a stale count for that issue

#### Scenario: A non-terminal issue contributes to no count

- **WHEN** an issue has not reached a terminal transition event
- **THEN** the issue SHALL NOT contribute to any completed or failed count

### Requirement: Completion counts are bucketed by terminal event time over a fixed window

The surface SHALL bucket the project's terminal transitions by the transition event's recorded time, not by the issue's creation or last-update time. The surface SHALL expose two fixed bucket granularities: a `day` granularity covering the 30 trailing UTC calendar days inclusive of today, and a `week` granularity covering the 12 trailing ISO weeks (Monday-anchored, UTC) inclusive of the current week. Every bucket in the chosen window SHALL be emitted, including buckets whose counts are zero. The bucket granularity SHALL be one of these two fixed values and SHALL NOT be user-configurable to any other size or custom time range.

#### Scenario: Counts are anchored on the terminal event time

- **WHEN** an issue was created 40 days ago but completed 3 days ago and the day granularity is requested
- **THEN** the issue's completed count SHALL appear in the bucket holding the completion event time (3 days ago)
- **AND** the count SHALL NOT appear in the bucket holding the creation time

#### Scenario: The day granularity covers 30 trailing UTC days inclusive of today

- **WHEN** the day granularity is requested
- **THEN** the surface SHALL emit 30 UTC-day buckets inclusive of today
- **AND** every bucket in the window SHALL be present, including zero-count buckets

#### Scenario: The week granularity covers 12 trailing ISO weeks inclusive of the current week

- **WHEN** the week granularity is requested
- **THEN** the surface SHALL emit 12 ISO-week buckets (Monday-anchored, UTC) inclusive of the current week
- **AND** every bucket in the window SHALL be present, including zero-count buckets

#### Scenario: The window advances with the current time

- **WHEN** the surface is requested at two times separated by more than one bucket
- **THEN** the window boundary SHALL move with the current time
- **AND** an issue whose terminal event time ages past the window between the two requests SHALL drop out of the surface

### Requirement: The surface returns a current-window total and a previous-adjacent-window total for trend derivation

To support deriving a completion trend without making the caller sum buckets, the surface SHALL return, in addition to the per-bucket series, a **current-window total** (the completed and failed counts aggregated over the current window) and a **previous-adjacent-window total** (the completed and failed counts aggregated over the immediately preceding window of the same length). The previous window SHALL be adjacent to and the same length as the current window, SHALL be anchored on terminal event time using the same classification, and SHALL advance with the current time so that it always represents the immediately preceding period. This return SHALL be strictly additive: the existing per-bucket series, window bounds, and bucket granularity SHALL be preserved unchanged.

#### Scenario: The previous window is the same length as and immediately precedes the current window

- **WHEN** the surface is requested with a current window of `[now - W, now]`
- **THEN** the surface SHALL also return a previous window of `[now - 2W, now - W]`
- **AND** the previous window SHALL aggregate completed and failed counts using the same terminal-event classification as the current window

#### Scenario: The current-window and previous-window totals are derived from the same classification

- **WHEN** the current window holds 5 completed and 1 failed issues and the previous window holds 3 completed and 0 failed
- **THEN** the current-window total SHALL be 5 completed and 1 failed
- **AND** the previous-window total SHALL be 3 completed and 0 failed

#### Scenario: The per-bucket series and granularity are preserved when the totals are added

- **WHEN** the current-window and previous-window totals are returned
- **THEN** the existing per-bucket series, window bounds, and fixed bucket granularity SHALL remain available and unchanged
- **AND** the totals SHALL be strictly additive to the existing response

### Requirement: A window with no terminal issues yields a defined empty result, not an error or a fabricated count

When a window (current or previous) contains no terminal issues, the surface SHALL return the defined empty (zero-sample) result for that window's totals rather than an error, an implicit zero count presented as a real sample, or a fabricated count. The empty result for the previous window SHALL be distinguishable by the consumer from a genuine zero-completion previous window, so a consumer can hide the trend (no baseline) rather than render a misleading "no change" arrow.

#### Scenario: No terminal issues in the previous window yields the empty result

- **WHEN** the previous adjacent window contains no terminal issues
- **THEN** the surface SHALL return the defined empty result for the previous window
- **AND** the response SHALL be successful (not an error)
- **AND** the empty result SHALL be distinguishable from a genuine zero-completion window

#### Scenario: A genuine zero-completion window is distinguishable from the empty result

- **WHEN** a window contains terminal issues all of which cancel and none complete such that the completed count is genuinely zero with a non-zero sample count
- **THEN** the surface SHALL report the genuine zero with a non-zero sample count
- **AND** this SHALL be distinguishable from the empty (zero-sample) result

### Requirement: Backend exposes the completion surface as an additive project-scoped read with no new data collection

The server SHALL expose a project-scoped HTTP read surface that returns the completion buckets, the current-window and previous-adjacent-window totals, and the empty/zero-sample state, computed purely from the already-recorded terminal transition events in the durable issue-event log. The surface SHALL NOT introduce any new lifecycle event, persisted field, domain write, or data-collection path; it is a strictly additive read over events the system already records. The surface SHALL return `200` with the defined empty result for any zero-sample window, and SHALL return `404` for an unknown project, consistent with the other project metrics endpoints. Unsupported bucket values SHALL be rejected.

#### Scenario: Client reads the completion surface for a project

- **WHEN** a client requests the completion surface for a project that has terminal issues within the window
- **THEN** the server SHALL return `200` with the per-bucket series and the current-window and previous-window totals
- **AND** every figure SHALL be computed only from the already-recorded terminal transition events

#### Scenario: An unsupported bucket value is rejected

- **WHEN** a client requests the completion surface with a bucket value other than `day` or `week`
- **THEN** the server SHALL reject the request
- **AND** the server SHALL NOT return a fabricated series

#### Scenario: Reading the surface introduces no new data collection and mutates no state

- **WHEN** a client reads the completion surface
- **THEN** no new event, persisted field, or data-collection path SHALL be introduced
- **AND** no issue, session, workflow, or approval domain state SHALL be written or mutated

#### Scenario: Unknown project returns not found

- **WHEN** a client requests the completion surface for a project reference that does not resolve to a known project
- **THEN** the server SHALL return `404`
