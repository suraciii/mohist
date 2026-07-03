### Requirement: Dedicated metrics service ownership

A dedicated issue metrics service (`IssueMetricsQuerier`) SHALL own all analytics aggregation: completion buckets, quality, approval-wait, delivery time, and stage durations. It SHALL also own their result records, the `CompletionBucket` enum, and the private accumulator/helper types that support them.

#### Scenario: Each metrics endpoint is served by the metrics service

- **WHEN** a metrics endpoint (completion, quality, approval-wait, delivery-time, stage-duration) is invoked
- **THEN** the result is produced by the dedicated `IssueMetricsQuerier`, not by the read-model query service

### Requirement: Metrics service is dependency-injectable as scoped

The metrics service SHALL be registered as a scoped service so it is injectable into the metrics route partials and any other consumer, matching the lifetime of the read-model query service.

#### Scenario: Metrics service resolves from the DI container

- **WHEN** a consumer requests `IssueMetricsQuerier` from the service scope
- **THEN** a scoped instance is returned and is distinct per scope, identical in lifetime semantics to other `IScopedService` registrations

### Requirement: Aggregation results are unchanged

The metrics service MUST NOT alter any aggregation formula, window definition, bucketing rule, result field shape, or result value. The relocation of methods and types from the read-model service SHALL be behavior-preserving for every aggregation.

#### Scenario: Completion buckets match the prior result

- **WHEN** the completion-buckets aggregation runs for a given project, bucket granularity, and anchor time
- **THEN** every bucket boundary, completed count, and failed count equals the result the read-model service produced before this change

#### Scenario: Quality metrics match the prior result

- **WHEN** the quality aggregation runs for a given project and anchor time
- **THEN** the 7-day window, 30-day window, and 30-day trend — including sample counts, first-time-right rates, and per-stage rework rates — equal the result the read-model service produced before this change

#### Scenario: Approval-wait metrics match the prior result

- **WHEN** the approval-wait aggregation runs for a given project and anchor time
- **THEN** the window, sample count, average, median, and max seconds equal the result the read-model service produced before this change

#### Scenario: Delivery-time metrics match the prior result

- **WHEN** the delivery-time aggregation runs for a given project and anchor time
- **THEN** the per-issue lead-days and cycle-days points (including null cycle-days for issues with no recorded work-start) equal the result the read-model service produced before this change

#### Scenario: Stage-duration metrics match the prior result

- **WHEN** the stage-duration aggregation runs for a given project and anchor time
- **THEN** the per-stage aggregates, flow-efficiency ratio, and wait breakout equal the result the read-model service produced before this change

### Requirement: Shared median calculation

The median calculation SHALL be defined exactly once within the metrics service. The approval-wait path MUST delegate to the single shared median implementation rather than maintaining an inline copy of the odd/even formula.

#### Scenario: Approval-wait median uses the shared implementation

- **WHEN** the approval-wait aggregation computes its median over a sorted sample set
- **THEN** it produces the same value the shared median implementation produces for that sample set, for both odd and even sample counts

### Requirement: Shared internal patterns within the metrics service

The metrics service's repeated internal patterns — scanning `IssueEvents` by project source, and loading-and-pairing workflow runs with their events — SHALL be single-implementation helpers shared across the metrics methods that need them, rather than each method re-implementing the loop.

#### Scenario: Project-source event scan is shared

- **WHEN** multiple metrics methods need to load and filter `IssueEvents` by the project's issue sources
- **THEN** they invoke a single shared scan helper rather than each containing its own copy of the load-and-filter loop

#### Scenario: Workflow-run load-and-pair is shared

- **WHEN** multiple metrics methods need to load workflow-run state and pair it with issue lifecycle data
- **THEN** they invoke a single shared load-and-pair helper rather than each re-implementing the discovery logic
