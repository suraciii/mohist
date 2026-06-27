### Requirement: First-time-right classifies a shipped issue by the absence of any check repair across its whole lifecycle

The system SHALL classify a shipped issue — one whose status is `Done` — as **first-time-right** if and only if no check, across every stage run of its workflow run, triggered a repair over the issue's whole lifecycle. A check SHALL be considered to have triggered a repair when that check's existing per-check repair count is greater than zero. A check whose repair count is zero SHALL NOT count as a repair. An issue that has not reached `Done` (is still in flight, failed, or otherwise not shipped) SHALL NOT participate in the first-time-right classification at all — first-time-right is a property of shipped work only.

#### Scenario: Shipped issue with zero repairs on every check is first-time-right

- **WHEN** an issue has status `Done`
- **AND** every check across all of its workflow run's stage runs has a repair count of zero
- **THEN** the issue SHALL be classified as first-time-right

#### Scenario: Shipped issue with any repaired check is not first-time-right

- **WHEN** an issue has status `Done`
- **AND** at least one check across its workflow run's stage runs has a repair count greater than zero
- **THEN** the issue SHALL be classified as not first-time-right (reworked)

#### Scenario: In-flight issue is excluded from first-time-right classification

- **WHEN** an issue has a status other than `Done`
- **THEN** the issue SHALL NOT be classified as either first-time-right or reworked
- **AND** it SHALL NOT contribute to the first-time-right rate

### Requirement: First-time-right rate is computed over shipped issues within the window

The system SHALL compute the first-time-right rate as the share of issues shipped (reached `Done`) within a trailing window that are first-time-right: the number of first-time-right shipped issues divided by the total number of issues shipped within the same window. The denominator SHALL be exactly the set of shipped issues within the window; issues that are not `Done` SHALL NOT contribute to either the numerator or the denominator. The rate SHALL be expressed as a proportion bounded between zero and one.

#### Scenario: Rate equals first-time-right shipped over all shipped in the window

- **WHEN** a trailing window contains ten shipped issues of which seven are first-time-right and three had at least one repaired check
- **THEN** the first-time-right rate SHALL be `7 / 10`

#### Scenario: Non-shipped issues do not dilute the rate

- **WHEN** a project has ten shipped issues and fifty in-flight issues within the window
- **THEN** the first-time-right rate denominator SHALL be ten
- **AND** the fifty in-flight issues SHALL NOT contribute to the numerator or the denominator

### Requirement: Per-stage rework classifies a stage by any check in that stage triggering repair

For each workflow stage, the system SHALL classify a stage-entered issue as **reworked at that stage** if and only if at least one check belonging to that stage triggered a repair — i.e. at least one check in that stage has a repair count greater than zero. A stage where every check has a repair count of zero SHALL NOT count as reworked. An issue that never entered a given stage SHALL NOT contribute to that stage's rework classification in any way — it is excluded from that stage's numerator and denominator.

#### Scenario: Stage with at least one repaired check is reworked

- **WHEN** an issue entered the `check` stage
- **AND** at least one check in the `check` stage has a repair count greater than zero
- **THEN** the issue SHALL count as reworked at the `check` stage

#### Scenario: Stage with no repaired checks is not reworked

- **WHEN** an issue entered the `build` stage
- **AND** every check in the `build` stage has a repair count of zero
- **THEN** the issue SHALL NOT count as reworked at the `build` stage

#### Scenario: Issue that never entered a stage is excluded from that stage's rate

- **WHEN** an issue never reached the `integrate` stage
- **THEN** the issue SHALL NOT contribute to the `integrate` stage's rework numerator
- **AND** the issue SHALL NOT contribute to the `integrate` stage's rework denominator

### Requirement: Per-stage rework rate is computed over shipped issues that entered the stage within the window

For each stage, the system SHALL compute the per-stage rework rate as the share of issues shipped (reached `Done`) within a trailing window that entered that stage where that stage was reworked: the number of shipped-in-window issues that entered the stage and were reworked at it, divided by the number of shipped-in-window issues that entered that stage. In-flight issues are outside this denominator until they ship, even if they have already entered the stage or triggered repair. Each stage SHALL produce its own independent rate. The denominator for a stage SHALL be the set of shipped-in-window issues that entered that stage, which MAY differ across stages.

#### Scenario: Per-stage rate equals reworked-over-entered for that stage

- **WHEN** within a trailing window twenty shipped issues entered the `plan` stage
- **AND** four of them were reworked at the `plan` stage
- **THEN** the `plan` stage rework rate SHALL be `4 / 20`

#### Scenario: Each stage reports an independent rate

- **WHEN** within a trailing window the `plan` stage has a rework rate of `4 / 20` and the `check` stage has a rework rate of `6 / 18`
- **THEN** the aggregation SHALL report each stage's rate separately
- **AND** the `plan` rate and `check` rate SHALL NOT be combined into a single number

#### Scenario: In-flight repaired issues are excluded from per-stage rework rates until shipped

- **WHEN** a project has one shipped issue that entered the `plan` stage without repair
- **AND** one in-flight issue has entered the `plan` stage and triggered repair
- **THEN** the `plan` stage denominator SHALL be `1`, counting only the shipped issue
- **AND** the `plan` stage rework rate SHALL be `0 / 1`

### Requirement: Quality metrics use trailing 7-day and 30-day windows anchored on ship time

The system SHALL compute both the first-time-right rate and every per-stage rework rate over two trailing windows: a **7-day** window `[now - 7d, now]` and a **30-day** window `[now - 30d, now]`. Window membership SHALL be anchored on when each issue shipped (reached `Done`): an issue contributes to a window if and only if it reached `Done` within that window. An issue that shipped more than 30 days ago SHALL be excluded from both windows. The 7-day rate and the 30-day rate SHALL be returned together so the consumer can compare recent and longer-term quality in a single read.

#### Scenario: 7-day and 30-day windows include different sets of shipped issues

- **WHEN** a project has shipped issues that reached `Done` 3 days ago, 20 days ago, and 40 days ago
- **THEN** the 7-day window SHALL include only the issue from 3 days ago
- **AND** the 30-day window SHALL include the issues from 3 days ago and 20 days ago
- **AND** the issue from 40 days ago SHALL be excluded from both windows

#### Scenario: Windows advance with the current time

- **WHEN** the aggregation is requested at two different times separated by more than a day
- **THEN** the window boundaries SHALL move with the current time
- **AND** an issue whose ship time ages past 7 days (or 30 days) between the two requests SHALL drop out of the corresponding window

### Requirement: Zero-sample quality aggregation returns a defined empty result distinguishable from a real rate

When a trailing window contains no shipped issues, the aggregation SHALL return a defined empty result for that window rather than an error or an implicit rate of zero or one. The empty result SHALL be distinguishable by the consumer from a genuine first-time-right rate of `1` (every shipped issue was first-time-right) and from a genuine rework rate of `0` (no stage was reworked), so a UI can render "no data yet" rather than a misleadingly perfect score. Each window (7-day and 30-day) SHALL be evaluated independently for emptiness.

#### Scenario: No shipped issues in a window yields an empty result

- **WHEN** a trailing window contains no issues that reached `Done`
- **THEN** the aggregation SHALL return a defined empty result for that window
- **AND** the result SHALL NOT be reported as a first-time-right rate of zero or one
- **AND** the response SHALL be successful (not an error)

#### Scenario: All-shipped-first-time-right is distinguishable from empty

- **WHEN** a trailing window contains five shipped issues and all five are first-time-right
- **THEN** the aggregation SHALL report a first-time-right rate of `1` with a non-zero sample count
- **AND** this SHALL be distinguishable from the empty (zero-sample) result

#### Scenario: Each window is evaluated for emptiness independently

- **WHEN** the 7-day window contains no shipped issues but the 30-day window contains several
- **THEN** the 7-day result SHALL be the empty result
- **AND** the 30-day result SHALL report the computed rates from its samples

### Requirement: Backend exposes a project-scoped AI quality aggregation endpoint with no new data collection

The server SHALL expose a project-scoped HTTP endpoint that returns the AI quality aggregation — the first-time-right rate and the per-stage rework rate for each stage — for both the trailing 7-day and 30-day windows. The endpoint SHALL be co-located with the existing project issue metrics surface (the completion and approval-wait metrics endpoints) so dashboards can fetch the summary in one read. The endpoint SHALL compute the aggregation purely from the existing per-check repair counts already recorded on workflow run stage checks; the change SHALL NOT introduce any new event, state collection, or workflow-domain write to support the endpoint. The zero-sample cases SHALL be returned as `200` with the defined empty result.

#### Scenario: Client reads both windows for a project

- **WHEN** a client requests the AI quality aggregation for a project that has shipped issues within both trailing windows
- **THEN** the server SHALL return `200` with the first-time-right rate and the per-stage rework rates for both the 7-day and 30-day windows
- **AND** every rate SHALL be computed only from issues that reached `Done` within the respective window

#### Scenario: Project with no shipped issues returns the empty result per window

- **WHEN** a client requests the AI quality aggregation for a project that has no shipped issues within a given trailing window
- **THEN** the server SHALL return `200` with the defined empty result for that window
- **AND** the response SHALL NOT report a zero or one rate for the empty window

#### Scenario: Aggregation introduces no new data collection

- **WHEN** the AI quality aggregation endpoint is invoked
- **THEN** the server SHALL compute the result from the already-recorded per-check repair counts on workflow run stage checks
- **AND** no new event, state collection, or workflow-domain write SHALL be introduced to support the endpoint
