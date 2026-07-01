## ADDED Requirements

### Requirement: Stage duration measures StageStarted-to-StageCompleted per reached stage for each delivered issue

The system SHALL derive, for every delivered issue (an issue that has reached the terminal `done` state), a per-stage **stage duration** for each workflow stage the issue reached, computed as the elapsed duration from that stage's `StageStarted` event time to that stage's matching `StageCompleted` event time. The stage identity SHALL be carried by the `Stage` payload of the durable `StageStarted` / `StageCompleted` workflow-run events (e.g. `plan`, `build`, `check`, `integrate`). A stage the issue never reached SHALL contribute no duration sample for that issue. A stage whose latest attempt has a `StageStarted` but no matching `StageCompleted` SHALL contribute an undefined (empty) duration sample for that stage, distinguishable from a genuine zero-duration stage. Stage durations SHALL be expressed as durations (seconds).

#### Scenario: A reached stage contributes its started-to-completed elapsed time

- **WHEN** a delivered issue's `check` stage emitted `StageStarted` at hour 1 and `StageCompleted` at hour 3
- **THEN** the issue's `check` stage duration SHALL equal the elapsed duration from the `StageStarted` time to the `StageCompleted` time
- **AND** the stage identity SHALL be sourced from the events' `Stage` payload

#### Scenario: A stage the issue never reached contributes no sample

- **WHEN** a delivered issue's workflow reached `plan`, `build`, and `check` but never reached `integrate`
- **THEN** the issue SHALL contribute a stage-duration sample for `plan`, `build`, and `check`
- **AND** the issue SHALL contribute no sample for `integrate`

#### Scenario: A started-but-never-completed latest attempt yields an undefined stage duration

- **WHEN** a delivered issue's latest attempt of a stage has a `StageStarted` with no matching `StageCompleted`
- **THEN** that stage SHALL contribute an undefined (empty) duration sample for that issue
- **AND** the undefined duration SHALL be distinguishable from a genuine zero-duration stage

### Requirement: Stage duration keeps the latest attempt per stage, matching the invalidate-on-restart idiom

When a stage has been attempted more than once for an issue — because of a `retry`, a `rerun`, or a `rerun-from-stage` that produced additional `StageStarted` / `StageCompleted` event pairs for the same stage, whether within one workflow run or across the issue's multiple workflow runs — the stage-duration sample SHALL be derived from the issue's **latest** attempt of that stage (the most recent `StageStarted` → matching `StageCompleted` pair for that stage), and SHALL NOT aggregate, average, or retain earlier invalidated attempts. This matches the existing invalidate-on-restart idiom, under which an invalidated earlier `StageRun` is superseded by its replacement rather than carried as a separate contribution.

#### Scenario: A re-attempted stage uses the latest attempt, not the earlier one

- **WHEN** a delivered issue's `build` stage was attempted once (started hour 1, completed hour 4), then re-attempted via a rerun (started hour 10, completed hour 12)
- **THEN** the issue's `build` stage duration SHALL be derived from the later attempt (started hour 10, completed hour 12)
- **AND** the earlier attempt (started hour 1, completed hour 4) SHALL NOT contribute to the sample

#### Scenario: Earlier invalidated attempts are not averaged in

- **WHEN** a delivered issue's `check` stage has a prior invalidated attempt of 5 hours and a latest attempt of 2 hours
- **THEN** the `check` stage-duration sample for that issue SHALL be 2 hours
- **AND** the sample SHALL NOT be the average (3.5 hours) or the sum (7 hours) of the two attempts

#### Scenario: The latest attempt is taken across the issue's workflow runs

- **WHEN** a delivered issue has multiple workflow runs and the `plan` stage was attempted in each, with the most recent run's `plan` attempt starting latest in time
- **THEN** the `plan` stage-duration sample SHALL be derived from the most recent run's `plan` attempt
- **AND** earlier runs' `plan` attempts SHALL NOT contribute

### Requirement: Per-stage distribution returns average and median over delivered issues with a per-stage sample count

Over the windowed delivered-issue population, the surface SHALL return, for each stage reached by at least one delivered issue, the **average** (arithmetic mean) and the **median** of the per-issue stage-duration samples for that stage, together with the **sample count** (the number of delivered issues contributing a defined duration sample for that stage). Undefined stage-duration samples (started-but-never-completed latest attempts) SHALL be excluded from the average and median, and SHALL NOT be treated as zero. The stages SHALL be returned in workflow stage order. A stage reached by no delivered issue in the window SHALL be absent from the result (not a fabricated zero).

#### Scenario: Average and median are computed per stage from the defined samples

- **WHEN** the windowed delivered-issue population yields `build` stage-duration samples of `[1h, 2h, 2h, 4h, 16h]`
- **THEN** the surface SHALL return an average of `5h` and a median of `2h` for the `build` stage
- **AND** the surface SHALL return a sample count of `5` for the `build` stage

#### Scenario: Undefined samples are excluded, not treated as zero

- **WHEN** a stage has three defined samples of `[2h, 4h, 6h]` and one undefined (started-but-never-completed) sample
- **THEN** the average and median SHALL be computed over the three defined samples only
- **AND** the undefined sample SHALL NOT pull the average toward zero

#### Scenario: Stages are ordered by workflow stage order

- **WHEN** the surface returns per-stage aggregates for a `plan` / `build` / `check` / `integrate` workflow
- **THEN** the stages SHALL be returned in the workflow's stage order
- **AND** a stage no delivered issue reached SHALL be absent rather than reported as a zero-duration stage

### Requirement: Cycle time decomposes into active-work, approval-gate wait, and inactive gaps

For each delivered issue with a defined cycle time (per the `issue-delivery-time-metrics` definition: earliest `IssueWorkStarted` to the latest terminal `done` completion), the surface SHALL decompose the cycle into three non-overlapping components that together sum to the cycle time:

- **active-work time** — the sum of the issue's latest-attempt stage durations (Σ `StageCompleted − StageStarted` per reached stage), minus the issue's approval-gate wait;
- **approval-gate wait** — the sum of elapsed `approvalState.requestedAt` → `approvalState.respondedAt` over the issue's **completed** approvals (`approved` or `rejected`), measured exactly as `approval-waiting-metrics` defines it; and
- **inactive-gap time** — the remainder of the cycle not covered by any stage's latest-attempt active span (the cycle time minus the sum of latest-attempt stage durations), capturing inter-stage gaps, pre-first-stage queue time, and post-last-stage time.

The three components SHALL be non-negative, and active-work time plus approval-gate wait plus inactive-gap time SHALL equal the issue's cycle time. Pending (`awaiting`) approvals SHALL NOT contribute to the approval-gate wait (they have no `respondedAt`), consistent with `approval-waiting-metrics`.

#### Scenario: The three components sum to the cycle time

- **WHEN** a delivered issue has a cycle time of 10 hours, latest-attempt stage durations summing to 7 hours, and completed-approval waits summing to 1 hour
- **THEN** the active-work time SHALL be 6 hours (7h stage time minus 1h approval wait)
- **AND** the inactive-gap time SHALL be 3 hours (10h cycle minus 7h stage time)
- **AND** active-work (6h) plus approval-gate wait (1h) plus inactive-gap (3h) SHALL equal the 10-hour cycle time

#### Scenario: Approval-gate wait reuses the approval-waiting-metrics definition

- **WHEN** a delivered issue has one `approved` approval (`requestedAt` → `respondedAt` of 2 hours) and one `awaiting` approval (no `respondedAt`)
- **THEN** the issue's approval-gate wait SHALL be 2 hours
- **AND** the `awaiting` approval SHALL contribute nothing to the wait

#### Scenario: An issue with no approval gates has zero approval-gate wait

- **WHEN** a delivered issue's workflow had no approval requests
- **THEN** the issue's approval-gate wait SHALL be zero
- **AND** the active-work time SHALL equal the sum of its latest-attempt stage durations

### Requirement: Flow-efficiency ratio is active-work time divided by cycle time

The surface SHALL return a single **flow-efficiency ratio** for the windowed delivered-issue population, computed as the sum of per-issue active-work time divided by the sum of per-issue cycle time, over delivered issues that have a defined and strictly positive cycle time. The ratio SHALL be expressed as a value in the range `[0, 1]`. A delivered issue whose cycle time is undefined (no recorded `IssueWorkStarted`) or zero SHALL be excluded from both the numerator and the denominator. The ratio SHALL be the population-weighted ratio (Σ active-work ÷ Σ cycle time), not the arithmetic average of per-issue ratios.

#### Scenario: The ratio is the population weighted ratio of active-work over cycle time

- **WHEN** the windowed population has two delivered issues — issue A with cycle 10h and active-work 6h, and issue B with cycle 20h and active-work 4h
- **THEN** the flow-efficiency ratio SHALL be (6h + 4h) ÷ (10h + 20h) = 10/30
- **AND** the ratio SHALL NOT be the average of the per-issue ratios (0.6 and 0.2)

#### Scenario: An issue entirely consumed by stage work with no wait yields full efficiency for that issue

- **WHEN** a delivered issue's latest-attempt stage durations exactly tile its cycle time and it has no approval-gate wait
- **THEN** that issue's active-work time SHALL equal its cycle time
- **AND** that issue SHALL contribute a per-issue active-work equal to its cycle time to the ratio

#### Scenario: Issues without a defined or positive cycle time are excluded from the ratio

- **WHEN** the windowed population contains a delivered issue with no recorded `IssueWorkStarted` (undefined cycle time)
- **THEN** that issue SHALL be excluded from both the numerator and the denominator of the flow-efficiency ratio
- **AND** the ratio SHALL NOT be computed by treating its undefined cycle time as zero

### Requirement: Wait breakout surfaces approval-gate wait and inactive gaps as averages per delivered issue

Alongside the flow-efficiency ratio, the surface SHALL return a **wait breakout** giving the average approval-gate wait per delivered issue and the average inactive-gap time per delivered issue, each computed over the windowed delivered-issue population that has a defined cycle time. Delivered issues with an undefined cycle time SHALL be excluded from the wait-breakout averages. The two averages SHALL be expressed as durations (seconds) and SHALL let the consuming chart present *why* flow efficiency is what it is — how much of a typical issue's cycle is spent waiting on approvals versus sitting inactive between stages.

#### Scenario: The breakout returns average approval-wait and average inactive-gap per issue

- **WHEN** the windowed population has three delivered issues with defined cycle times whose approval-gate waits are `[1h, 2h, 3h]` and inactive gaps are `[2h, 4h, 6h]`
- **THEN** the surface SHALL return an average approval-gate wait of `2h` and an average inactive-gap of `4h`
- **AND** both averages SHALL be computed over the same three-issue population

#### Scenario: An issue with no wait contributes zero to the averages, not exclusion

- **WHEN** a delivered issue with a defined cycle time has no approvals and no inactive gaps
- **THEN** that issue SHALL contribute a zero approval-gate wait and a zero inactive-gap to the averages
- **AND** the issue SHALL NOT be excluded from the wait-breakout averages solely because its wait is zero

### Requirement: Stage-duration surface is windowed by completion time over a fixed trailing window shared with delivery time

The stage-duration, flow-efficiency, and wait-breakout aggregates SHALL be scoped to delivered issues whose persisted completion time (the terminal `done` moment) falls within a single fixed trailing window `[now - W, now]`. The window length `W` SHALL be the same fixed length the `issue-delivery-time-metrics` surface uses, SHALL NOT be user-configurable, and SHALL be anchored on completion time (not creation or last-update time). Windowing SHALL be keyed on completion time so the surface reflects the project's recent delivery experience and so the consuming Productivity-zone charts share the same delivered-issue population as the cycle-time scatter.

#### Scenario: Only issues delivered within the trailing window contribute

- **WHEN** a project has delivered issues with completion times of 5 days ago, 25 days ago, and 90 days ago, and the fixed trailing window is shorter than 90 days
- **THEN** the surface SHALL include only the issues whose completion time falls within the window
- **AND** the issue delivered 90 days ago SHALL be excluded

#### Scenario: The window is fixed and not configurable

- **WHEN** the stage-duration surface is requested
- **THEN** the trailing window length SHALL be a single fixed value
- **AND** the window length SHALL NOT be configurable by the caller

### Requirement: Zero delivered issues in the window yields a defined empty result

When the trailing window contains no delivered issues, the surface SHALL return a defined empty result rather than an error, an implicit zero duration, or a fabricated sample. The empty result SHALL be distinguishable by the consumer from a genuine computed value (a non-zero sample count, a genuine zero-duration stage, or a genuine zero-or-one ratio) so the consuming chart can render "no data yet" rather than a misleading "instant stages" or "100% flow efficiency".

#### Scenario: No delivered issues in the window yields an empty result, not an error

- **WHEN** the trailing window contains no delivered issues (only non-terminal issues, cancelled issues, or no issues at all)
- **THEN** the surface SHALL return the defined empty result
- **AND** the response SHALL be successful (not an error)
- **AND** the result SHALL NOT report a numeric zero duration or a flow-efficiency ratio

#### Scenario: A genuine zero-duration stage is distinguishable from the empty result

- **WHEN** a delivered issue in the window yields a genuine zero-duration stage (the same `StageStarted` and `StageCompleted` moment)
- **THEN** the surface SHALL report that stage's zero duration as a real computed value with a non-zero sample count
- **AND** this SHALL be distinguishable from the empty (zero-sample) result

### Requirement: Stage-duration aggregation is read-only and introduces no new data collection

The stage-duration surface SHALL be computed purely from already-persisted events — the durable workflow-run `StageStarted` / `StageCompleted` events, the issue lifecycle `IssueWorkStarted` and terminal `IssueWorkCompleted` events and the completion time they populate, and the existing `approvalState.requestedAt` / `approvalState.respondedAt` timestamps. Computing and reading the surface SHALL NOT introduce any new lifecycle or workflow event, any new persisted field, any domain write, or any new data-collection path; it SHALL NOT mutate issue, session, workflow, or approval state. The surface is a strictly additive read over events the system already records.

#### Scenario: Durations are derived only from already-persisted events

- **WHEN** the stage-duration surface is computed and returned
- **THEN** every stage duration, the flow-efficiency ratio, and the wait breakout SHALL be derived from already-persisted workflow-run, lifecycle, and approval timestamps
- **AND** no new event, persisted field, or data-collection path SHALL be introduced to support the surface

#### Scenario: Reading the surface mutates no domain state

- **WHEN** a client reads the stage-duration surface
- **THEN** no issue, session, workflow, or approval domain state SHALL be written or mutated
- **AND** the read SHALL be side-effect-free

### Requirement: Backend exposes the stage-duration surface as an additive project-scoped read

The server SHALL expose a project-scoped HTTP read surface that returns the per-stage duration distribution (average, median, and sample count per stage), the flow-efficiency ratio, and the wait breakout (average approval-gate wait and average inactive-gap per delivered issue), over the fixed trailing window, computed purely from already-persisted events. The surface SHALL be additive: it SHALL NOT alter, remove, or re-shape the existing project issue-metrics contracts, including the delivery-time, approval-wait, and quality surfaces. Zero delivered issues in the window SHALL be returned as `200` with the defined empty result, not as an error. The surface SHALL return `404` for an unknown project, consistent with the existing project metrics endpoints.

#### Scenario: Client reads the stage-duration surface for a project

- **WHEN** a client requests the stage-duration surface for a project that has delivered issues within the trailing window
- **THEN** the server SHALL return `200` with the per-stage average and median durations, the per-stage sample counts, the flow-efficiency ratio, and the wait breakout
- **AND** the aggregation SHALL be computed only from already-persisted events

#### Scenario: Project with no delivered issues in the window returns the empty result

- **WHEN** a client requests the stage-duration surface for a project that has no delivered issues within the trailing window
- **THEN** the server SHALL return `200` with the defined empty result
- **AND** the response SHALL NOT report a numeric zero duration or a flow-efficiency ratio

#### Scenario: Existing project issue-metrics contracts are preserved

- **WHEN** the stage-duration surface is added
- **THEN** the existing delivery-time, approval-wait, and quality surfaces and any other existing project issue-metrics contract SHALL remain available and unchanged
- **AND** the stage-duration surface SHALL be strictly additive

#### Scenario: Unknown project returns not found

- **WHEN** a client requests the stage-duration surface for a project reference that does not resolve to a known project
- **THEN** the server SHALL return `404`
