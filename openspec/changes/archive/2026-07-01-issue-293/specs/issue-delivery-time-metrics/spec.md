## ADDED Requirements

### Requirement: Lead time measures creation-to-completion per delivered issue

The system SHALL compute a per-issue **lead time** for every delivered issue — an issue that has reached the terminal `done` state via its `IssueWorkCompleted` event — as the elapsed duration from the issue's `IssueCreated` event time to the issue's persisted completion time. Lead time SHALL always be anchored on the original creation moment, which never changes across retries or reopens, so the lead time always spans creation to final completion. A delivered issue SHALL contribute exactly one lead-time sample. Issues that have not reached `done` — including issues in `cancelled` — SHALL NOT contribute a lead-time sample to this surface.

#### Scenario: Lead time spans creation to completion for a delivered issue

- **WHEN** a delivered issue was created on day 1 and reached `done` on day 4
- **THEN** the issue's lead time SHALL equal the elapsed duration from its `IssueCreated` event time to its completion time
- **AND** the issue SHALL contribute exactly one lead-time sample

#### Scenario: Cancelled issues do not contribute a lead-time sample

- **WHEN** an issue reached the terminal `cancelled` state (its `IssueClosed` event) instead of `done`
- **THEN** that issue SHALL NOT contribute a lead-time sample to the delivery-time surface
- **AND** only issues that reached `done` SHALL contribute

#### Scenario: Lead time keeps the original creation anchor across retries

- **WHEN** a delivered issue was created on day 1, started and failed, then re-started and reached `done` on day 10
- **THEN** the issue's lead time SHALL be anchored on the day-1 `IssueCreated` event time
- **AND** the lead time SHALL NOT be re-anchored to any later retry or work-start moment

### Requirement: Cycle time measures first-work-start-to-final-completion, surviving retries

The system SHALL compute a per-issue **cycle time** for every delivered issue as the elapsed duration from the issue's **earliest** `IssueWorkStarted` event time to the issue's persisted completion time (the final, latest terminal `done` moment). When an issue has had multiple work attempts (retries / reruns), cycle time SHALL span from the first work-start to the final completion, not from the latest attempt in isolation; the earliest work-start anchor SHALL be retained across retries. The completion anchor SHALL be the latest terminal moment, consistent with the persisted completion time, so that a reopen followed by a re-completion moves the completion anchor forward while preserving the earliest work-start.

#### Scenario: Cycle time spans earliest work-start to final completion across retries

- **WHEN** a delivered issue started work on day 2, was retried with a new work-start on day 5, and reached `done` on day 9
- **THEN** the issue's cycle time SHALL be measured from the earliest (day-2) `IssueWorkStarted` to the (day-9) completion
- **AND** the cycle time SHALL NOT be measured from the latest (day-5) work-start in isolation

#### Scenario: Reopen and re-completion moves the completion anchor, keeping the earliest work-start

- **WHEN** a delivered issue first reached `done` on day 6, was reopened, then reached `done` again on day 12, with its earliest work-start on day 2
- **THEN** the cycle time SHALL be measured from the day-2 earliest work-start to the day-12 latest completion
- **AND** the prior day-6 completion SHALL NOT be used as the completion anchor

#### Scenario: A delivered issue with no recorded work-start yields an undefined cycle time

- **WHEN** a delivered issue has reached `done` but has no recorded `IssueWorkStarted` event
- **THEN** the issue's cycle time SHALL be the undefined (empty) result
- **AND** the issue's lead time SHALL still be a defined duration
- **AND** the undefined cycle time SHALL be distinguishable from a genuine zero-duration cycle time

### Requirement: Delivery-time surface is windowed by completion time over a fixed trailing window

The per-issue lead-time and cycle-time samples SHALL be scoped to delivered issues whose persisted completion time falls within a fixed trailing window `[now - W, now]`, where `W` is a single fixed window length that is NOT user-configurable. Windowing SHALL be keyed on the issue's completion time (when the issue was delivered), not on the issue's creation time or last-update time, so the surface reflects the project's recent delivery speed. The window boundary SHALL advance with the current time: a delivered issue whose completion time ages past the window SHALL drop out of the surface.

#### Scenario: Only issues delivered within the trailing window are returned

- **WHEN** a project has delivered issues with completion times of 5 days ago, 25 days ago, and 90 days ago, and the fixed trailing window is shorter than 90 days
- **THEN** the surface SHALL include only the issues whose completion time falls within the window
- **AND** the issue delivered 90 days ago SHALL be excluded because its completion time falls outside the window

#### Scenario: The window is fixed and not user-configurable

- **WHEN** the delivery-time surface is requested
- **THEN** the trailing window length SHALL be a single fixed value
- **AND** the window length SHALL NOT be configurable by the caller

#### Scenario: The window advances with the current time

- **WHEN** the surface is requested at two different times separated by more than the window length
- **THEN** the window boundary SHALL move with the current time
- **AND** a delivered issue whose completion time ages past the window between the two requests SHALL drop out of the surface

### Requirement: Delivery-time surface exposes a per-issue series of completion date, lead time, and cycle time

The surface SHALL return, for each delivered issue in the trailing window, the issue's completion date, its lead-time duration, and its cycle-time duration (or the defined empty/undefined marker when the issue has no recorded work-start). The series SHALL be at per-issue granularity, which is the granularity the consuming chart requires to plot one scatter point per delivered issue and to compute rolling percentile overlays; the surface SHALL NOT pre-aggregate the samples into a single summary statistic. Durations SHALL be expressed in days.

#### Scenario: The surface returns one entry per delivered issue in the window

- **WHEN** the surface is requested for a project that has delivered issues within the trailing window
- **THEN** the surface SHALL return one entry per delivered issue
- **AND** each entry SHALL carry the issue's completion date, lead-time duration, and cycle-time duration (or the undefined marker)

#### Scenario: An issue without a work-start carries an undefined cycle time alongside a defined lead time

- **WHEN** the surface returns an entry for a delivered issue that has no recorded `IssueWorkStarted`
- **THEN** the entry SHALL carry a defined lead-time duration
- **AND** the entry SHALL carry the undefined (empty) marker for cycle time
- **AND** the undefined cycle time SHALL be distinguishable from a genuine zero cycle time

#### Scenario: The surface does not collapse the series into a single summary

- **WHEN** the surface is requested
- **THEN** the surface SHALL return per-issue samples
- **AND** the surface SHALL NOT return only an aggregate statistic (such as a single average) in place of the per-issue series

### Requirement: Zero delivered issues in the window yields a defined empty result

When the trailing window contains no delivered issues, the surface SHALL return a defined empty result rather than an error, an implicit zero duration, or a single fabricated sample. The empty result SHALL be distinguishable by the consumer from a genuine computed duration so the consuming chart can render "no data yet" rather than a misleading "instant delivery".

#### Scenario: No delivered issues in the window yields an empty result, not an error

- **WHEN** the trailing window contains no delivered issues (only non-terminal issues, cancelled issues, or no issues at all)
- **THEN** the surface SHALL return the defined empty result
- **AND** the response SHALL be successful (not an error)
- **AND** the result SHALL NOT report a numeric zero duration

#### Scenario: A genuine zero-duration delivery is distinguishable from the empty result

- **WHEN** a delivered issue in the window yields a genuine zero cycle time (the same earliest work-start and completion moment)
- **THEN** the surface SHALL report that issue's zero cycle time as a real computed value with a non-zero sample count
- **AND** this SHALL be distinguishable from the empty (zero-sample) result

### Requirement: Delivery-time aggregation is read-only and introduces no new data collection

The delivery-time surface SHALL be computed purely from already-persisted issue lifecycle events — the `IssueCreated`, `IssueWorkStarted`, and terminal `IssueWorkCompleted` events, and the completion time they already populate. Computing and reading the surface SHALL NOT introduce any new lifecycle event, any new persisted field, any domain write, or any new data-collection path; it SHALL NOT mutate issue, session, workflow, or approval state. The surface is a strictly additive read over events the system already records.

#### Scenario: Durations are derived only from already-persisted lifecycle events

- **WHEN** the delivery-time surface is computed and returned
- **THEN** every lead time and cycle time SHALL be derived from the already-persisted `IssueCreated`, `IssueWorkStarted`, and `IssueWorkCompleted` events and the completion time they already populate
- **AND** no new event, persisted field, or data-collection path SHALL be introduced to support the surface

#### Scenario: Reading the surface mutates no domain state

- **WHEN** a client reads the delivery-time surface
- **THEN** no issue, session, workflow, or approval domain state SHALL be written or mutated
- **AND** the read SHALL be side-effect-free

### Requirement: Backend exposes the delivery-time surface as an additive project-scoped read

The server SHALL expose a project-scoped HTTP read surface that returns the per-issue delivery-time series (completion date, lead time, cycle time, and the empty/undefined markers) over the fixed trailing window, computed purely from already-persisted issue lifecycle events. The surface SHALL be additive: it SHALL NOT alter, remove, or re-shape the existing project issue-metrics contracts, including the existing completion-metrics surface. Zero delivered issues in the window SHALL be returned as `200` with the defined empty result, not as an error. The surface SHALL return `404` for an unknown project, consistent with the existing project metrics endpoints.

#### Scenario: Client reads the per-issue delivery-time series for a project

- **WHEN** a client requests the delivery-time surface for a project that has delivered issues within the trailing window
- **THEN** the server SHALL return `200` with one entry per delivered issue carrying completion date, lead time, and cycle time
- **AND** the aggregation SHALL be computed only from already-persisted lifecycle events

#### Scenario: Project with no delivered issues in the window returns the empty result

- **WHEN** a client requests the delivery-time surface for a project that has no delivered issues within the trailing window
- **THEN** the server SHALL return `200` with the defined empty result
- **AND** the response SHALL NOT report a numeric zero duration

#### Scenario: Existing project issue-metrics contracts are preserved

- **WHEN** the delivery-time surface is added
- **THEN** the existing completion-metrics surface and any other existing project issue-metrics contract SHALL remain available and unchanged
- **AND** the delivery-time surface SHALL be strictly additive

#### Scenario: Unknown project returns not found

- **WHEN** a client requests the delivery-time surface for a project reference that does not resolve to a known project
- **THEN** the server SHALL return `404`
