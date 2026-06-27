### Requirement: Approval waiting time measures elapsed approval-gate time per completed approval

The system SHALL measure approval waiting time as the elapsed time from an approval's `approvalState.requestedAt` to its `approvalState.respondedAt`, for each **completed** approval whose `approvalState.status` is `approved` or `rejected` (i.e. `respondedAt` is present). Only the approval-gate wait SHALL be measured; durations of any other workflow stage SHALL NOT contribute. A currently pending approval whose `approvalState.status` is `awaiting` SHALL NOT participate in the wait-time aggregate at all — it has no `respondedAt` and is surfaced separately as an attention item, not as a wait-time sample.

#### Scenario: Completed approval contributes its requested-to-responded elapsed time

- **WHEN** an issue's approval has `approvalState.status` of `approved` or `rejected` with both `requestedAt` and `respondedAt` present
- **THEN** that approval SHALL contribute a single wait sample equal to `respondedAt - requestedAt`
- **AND** durations of any other workflow stage SHALL NOT contribute to the sample

#### Scenario: Pending approval is excluded from the wait-time aggregate

- **WHEN** an issue's approval has `approvalState.status` of `awaiting` (no `respondedAt`)
- **THEN** that approval SHALL NOT contribute a wait sample to the aggregate
- **AND** it SHALL continue to be surfaced as an individual attention item rather than in the wait-time metric

### Requirement: Approval waiting time aggregation uses a trailing 7-day window keyed on respondedAt

The aggregate SHALL be computed over a trailing 7-day window, including exactly those completed approvals whose `respondedAt` falls within the window `[now - 7d, now]`. Windowing SHALL be based on `respondedAt` (when the approval was acted on) so the metric reflects the user's recent approval responsiveness. Approvals with a `respondedAt` older than 7 days SHALL be excluded from the aggregate even if they are completed.

#### Scenario: Only approvals responded to within the trailing 7 days are aggregated

- **WHEN** a project has completed approvals with `respondedAt` values of 1 day ago, 6 days ago, and 10 days ago
- **THEN** the aggregate SHALL include only the approvals from 1 day ago and 6 days ago
- **AND** the approval from 10 days ago SHALL be excluded from the aggregate

#### Scenario: Window advances with the current time

- **WHEN** the aggregate is requested at two different times separated by more than a day
- **THEN** the window boundary SHALL move with the current time
- **AND** an approval whose `respondedAt` ages past 7 days between the two requests SHALL drop out of the aggregate

### Requirement: Aggregation returns average, median, and maximum statistics

Over the set of completed-approval wait samples within the trailing 7-day window, the aggregation SHALL return three statistics: the **average** (arithmetic mean) wait, the **median** wait, and the **maximum** (longest) wait. Each statistic SHALL be expressed as a duration. The aggregation SHALL compute all three statistics from the same sample set so the consumer can compare central tendency, typical case, and worst case in a single read.

#### Scenario: Average, median, and max are all returned from the same sample set

- **WHEN** the trailing 7-day window contains completed approvals with wait samples of `[1h, 2h, 2h, 4h, 16h]`
- **THEN** the aggregation SHALL return an average of `5h`
- **AND** the aggregation SHALL return a median of `2h`
- **AND** the aggregation SHALL return a maximum of `16h`

#### Scenario: Single sample yields identical average, median, and max

- **WHEN** the trailing 7-day window contains exactly one completed approval with a wait of `3.2h`
- **THEN** the average, median, and maximum SHALL each be `3.2h`

### Requirement: Zero-sample aggregation returns a defined empty result

When the trailing 7-day window contains no completed approvals, the aggregation SHALL return a defined empty result rather than an error or an implicit zero. The empty result SHALL be distinguishable by the consumer from a genuine average of `0` so a UI can render "no data yet" rather than "instant approvals".

#### Scenario: No completed approvals in window yields an empty result

- **WHEN** the trailing 7-day window contains no completed approvals (only pending approvals, or no approvals at all)
- **THEN** the aggregation SHALL return a defined empty result indicating zero samples
- **AND** the result SHALL NOT be reported as an average of zero duration
- **AND** the response SHALL be successful (not an error)

#### Scenario: Empty result is distinguishable from a real zero-duration wait

- **WHEN** one completed approval in the window has `requestedAt` equal to `respondedAt` (a genuine zero-duration wait)
- **THEN** the aggregation SHALL report a zero-sample-count of `1` with an average of `0`
- **AND** this SHALL be distinguishable from the empty (zero-sample) result

### Requirement: Backend exposes a project-scoped approval wait time aggregation endpoint

The server SHALL expose a project-scoped HTTP endpoint that returns the approval waiting time aggregation (average, median, maximum, and sample count) for the trailing 7-day window, computed from the existing `approvalState.requestedAt` / `approvalState.respondedAt` timestamps of the project's issues. The endpoint SHALL NOT introduce any new data collection — it SHALL aggregate purely over already-populated approval timestamps. The endpoint SHALL be co-located with the existing project issue metrics surface so dashboards can fetch the summary in one read.

#### Scenario: Client reads the trailing 7-day aggregation for a project

- **WHEN** a client requests the approval wait aggregation for a project that has completed approvals within the trailing 7 days
- **THEN** the server SHALL return `200` with the average, median, maximum, and sample count
- **AND** the aggregation SHALL be computed only from completed approvals (`approved` or `rejected`) whose `respondedAt` falls within the trailing 7 days

#### Scenario: Project with no qualifying approvals returns the empty result

- **WHEN** a client requests the approval wait aggregation for a project that has no completed approvals within the trailing 7-day window
- **THEN** the server SHALL return `200` with the defined empty result
- **AND** the response SHALL indicate zero samples without reporting a zero-duration average

#### Scenario: Aggregation introduces no new data collection

- **WHEN** the approval wait aggregation endpoint is invoked
- **THEN** the server SHALL compute the result from already-populated `approvalState.requestedAt` and `approvalState.respondedAt` timestamps
- **AND** no new event, state collection, or approval-domain write SHALL be introduced to support the endpoint
