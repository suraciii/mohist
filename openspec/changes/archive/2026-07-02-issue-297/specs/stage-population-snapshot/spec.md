## ADDED Requirements

### Requirement: A daily stage-population snapshot records per-stage counts for each project per day

The system SHALL persist, for each project and for each snapshot day, exactly one **stage-population snapshot** recording the count of issues attributed to each workflow stage as of that day. The recorded stages SHALL be the full ordered stage set the cumulative flow diagram presents: `backlog`, `plan`, `build`, `check`, `integrate`, and `done`. Each snapshot SHALL be scoped to exactly one project and one day, and SHALL record one count per stage. An issue SHALL be counted in at most one stage per snapshot day (its attributed stage). The snapshot is the persisted cache the cumulative flow diagram reads; the diagram SHALL NOT recompute per-day populations from the event stream on render.

#### Scenario: One snapshot row records one count per stage per project per day

- **WHEN** the daily snapshot for a project on a given day is produced
- **THEN** the system SHALL persist exactly one snapshot row for that project and day
- **AND** the row SHALL record one count for each of `backlog`, `plan`, `build`, `check`, `integrate`, and `done`
- **AND** each count SHALL equal the number of issues attributed to that stage as of that day

#### Scenario: An issue is counted in at most one stage per snapshot day

- **WHEN** a snapshot day is recorded for a project
- **THEN** each issue SHALL contribute to the count of at most one stage for that day
- **AND** the sum of the per-stage counts SHALL equal the number of issues in the project's flow population as of that day

### Requirement: Stage attribution assigns each in-flow issue to exactly one stage as of the snapshot day

For each issue in the project's flow population as of a snapshot day, the system SHALL attribute the issue to exactly one stage based on its persisted lifecycle and workflow-run events timestamped on or before that day. An issue SHALL be attributed to `backlog` when no `IssueWorkStarted` has occurred on or before the snapshot day. An issue SHALL be attributed to `done` when its latest terminal `IssueWorkCompleted` (`done`) occurred on or before the snapshot day. Otherwise — when work has started but the issue has not reached `done` as of the snapshot day — the issue SHALL be attributed to the workflow stage (`plan` / `build` / `check` / `integrate`) it has most recently entered as of that day, under the latest-attribution rule defined in the following requirement. An issue that has reached a terminal `IssueCancelled` state on or before the snapshot day SHALL be excluded from the flow population and SHALL NOT be counted in any stage.

#### Scenario: An issue whose work has not started is attributed to backlog

- **WHEN** an issue exists as of the snapshot day but its `IssueWorkStarted` occurs after that day (or has not occurred)
- **THEN** the issue SHALL be attributed to `backlog` for that snapshot day
- **AND** the issue SHALL increment the `backlog` count

#### Scenario: A completed issue is attributed to done

- **WHEN** an issue's latest terminal `done` completion occurred on or before the snapshot day
- **THEN** the issue SHALL be attributed to `done` for that snapshot day
- **AND** the issue SHALL increment the `done` count

#### Scenario: An in-flight issue is attributed to the stage it most recently entered

- **WHEN** an issue has started work but has not reached `done` as of the snapshot day, and has entered `build` (latest attempt started on or before the day) without yet entering a later stage
- **THEN** the issue SHALL be attributed to `build` for that snapshot day
- **AND** the issue SHALL increment the `build` count

#### Scenario: A cancelled issue is excluded from the population

- **WHEN** an issue has reached a terminal `IssueCancelled` state on or before the snapshot day
- **THEN** the issue SHALL NOT be counted in any stage for that snapshot day
- **AND** the issue SHALL be excluded from the flow population

### Requirement: Stage attribution follows the latest-attempt, latest-run-wins, invalidate-on-restart idiom

When a stage has been attempted more than once for an issue — through a retry, a rerun, or a rerun-from-stage, whether within one workflow run or across the issue's multiple workflow runs — the attribution SHALL follow the issue's **latest** attempt of each stage and its **latest** (non-invalidated) workflow run, matching the invalidate-on-restart idiom established by `workflow-stage-duration-metrics`. An invalidated earlier attempt or an earlier invalidated run SHALL be superseded by its replacement and SHALL NOT contribute to the attribution. A re-attempted or re-run stage SHALL NOT cause the issue to be counted more than once for a snapshot day. The attribution population SHALL stay internally consistent with the `workflow-stage-duration-metrics` stage population, so the stage the duration surface treats as the issue's latest is the same stage the snapshot attributes the issue to.

#### Scenario: A re-attempted stage attributes the issue to the latest attempt only

- **WHEN** an issue's `build` stage was attempted once (started day 1) and re-attempted via a rerun (started day 3), and a snapshot is taken for day 3 before the re-attempted `build` completes
- **THEN** the issue SHALL be attributed to `build` for day 3
- **AND** the issue SHALL be counted once, not twice, in the `build` count

#### Scenario: A rerun-from-stage moves the issue back to the rerun's stage

- **WHEN** an issue had progressed through `plan`, `build`, and `check`, then a rerun-from `plan` restarts from `plan` on or before the snapshot day (invalidating the later progress)
- **THEN** the issue SHALL be attributed to `plan` (or the furthest stage the rerun has re-entered) as of the snapshot day
- **AND** the invalidated `build` / `check` progress SHALL NOT attribute the issue to those stages

#### Scenario: The latest workflow run wins across the issue's multiple runs

- **WHEN** an issue has multiple workflow runs and an earlier run's stage attempt is the most recent only within that earlier (now invalidated) run
- **THEN** the attribution SHALL follow the latest non-invalidated run's stage progression
- **AND** the earlier run's stage attempts SHALL NOT contribute once superseded

#### Scenario: The attribution stays consistent with the stage-duration population

- **WHEN** the snapshot attributes an issue to a stage as of a day
- **THEN** that stage SHALL be the issue's latest non-invalidated stage under the same idiom the `workflow-stage-duration-metrics` surface uses
- **AND** the two surfaces SHALL NOT disagree on which stage is the issue's latest

### Requirement: Snapshots accumulate forward from go-live with no historical backfill

The snapshot history SHALL accumulate one day at a time from the day the snapshot mechanism goes live forward. The system SHALL NOT backfill snapshots for days before the go-live day. The cumulative flow diagram's history SHALL therefore grow one day at a time as new daily snapshots land, and SHALL NOT be reconstructed for the pre-go-live past.

#### Scenario: The first snapshot is the go-live day, not earlier

- **WHEN** the snapshot mechanism goes live for a project
- **THEN** the first snapshot SHALL cover the go-live day (or the first day the daily job runs thereafter)
- **AND** no snapshot SHALL be persisted for any day before go-live

#### Scenario: History grows one day at a time as snapshots land

- **WHEN** the project has been live for N days since go-live
- **THEN** the snapshot series SHALL span at most N days of history
- **AND** the series SHALL NOT contain reconstructed pre-go-live days

### Requirement: A daily background job produces the snapshot following the reconciliation-service pattern with idempotent writes

The system SHALL produce each daily stage-population snapshot via a background job that follows the existing `IssueWorkflowReconciliationService` `BackgroundService` pattern: a tunable period (configurable to a static value for tests), and a sweep-then-write cycle that derives each in-flow issue's attributed stage from already-persisted events and writes the snapshot row. A write for a given project and snapshot day SHALL be idempotent: producing the snapshot for the same project and day more than once SHALL NOT create duplicate rows and SHALL yield the same per-stage counts. The job SHALL NOT require a user trigger and SHALL NOT mutate any existing domain state; the snapshot row is the only new write.

#### Scenario: The job period is tunable for tests

- **WHEN** the daily snapshot job is configured
- **THEN** the job's sweep period SHALL be tunable, including to a static value for tests
- **AND** tests SHALL be able to drive a snapshot cycle without waiting on wall-clock time

#### Scenario: Repeated writes for the same project and day are idempotent

- **WHEN** the daily job produces a snapshot for a project and day it has already snapshotted
- **THEN** the system SHALL NOT create a duplicate snapshot row
- **AND** the per-stage counts for that project and day SHALL remain unchanged

#### Scenario: The job writes only the snapshot and mutates no existing domain state

- **WHEN** the daily job runs its sweep-then-write cycle
- **THEN** the only new write SHALL be the snapshot row
- **AND** no issue, session, workflow-run, or approval domain state SHALL be mutated

### Requirement: Snapshot computation reads only already-persisted events and touches no existing contract

The stage attribution and per-stage counts SHALL be derived purely from events the system already persists — the issue lifecycle events (`IssueWorkStarted`, terminal `IssueWorkCompleted` / `IssueCancelled`), the durable workflow-run stage events (per-run `StageStarted` / `StageCompleted`), and the issue's creation time. Computing the snapshot SHALL NOT introduce any new lifecycle or workflow event, any new persisted field on existing entities, or any new data-collection path. The snapshot storage SHALL be new persistence isolated from existing tables and SHALL NOT alter, remove, or re-shape any existing contract.

#### Scenario: Attribution is derived only from already-persisted events

- **WHEN** a snapshot is computed for a day
- **THEN** every per-stage count SHALL be derived from already-persisted lifecycle and workflow-run events
- **AND** no new event, persisted field on existing entities, or data-collection path SHALL be introduced

#### Scenario: The snapshot storage is isolated from existing persistence

- **WHEN** the snapshot mechanism is introduced
- **THEN** the snapshot SHALL reside in new persistence isolated from existing tables
- **AND** no existing persistence contract SHALL be altered, removed, or re-shaped

### Requirement: An additive project-scoped read surface returns the snapshot series over a fixed trailing window

The server SHALL expose an additive, project-scoped HTTP read surface that returns the stage-population snapshot series — the ordered list of daily snapshots, each carrying its day and its per-stage counts — over a single fixed trailing window. The window length SHALL NOT be user-configurable. The surface SHALL be strictly additive: it SHALL NOT alter, remove, or re-shape any existing project metrics contract, including the stage-duration, delivery-time, and cost surfaces. When no snapshots exist yet within the window (for example, before the first daily snapshot has landed), the surface SHALL return a successful response with a defined empty series rather than an error or a fabricated snapshot. The surface SHALL return `404` for an unknown project, consistent with the existing project metrics endpoints.

#### Scenario: Client reads the snapshot series for a project over the trailing window

- **WHEN** a client requests the snapshot series for a project that has snapshots within the trailing window
- **THEN** the server SHALL return `200` with the ordered daily snapshots, each carrying its day and per-stage counts
- **AND** the series SHALL span the fixed trailing window

#### Scenario: No snapshots yet yields a successful empty series

- **WHEN** a client requests the snapshot series for a project before any daily snapshot has landed
- **THEN** the server SHALL return `200` with a defined empty series
- **AND** the response SHALL NOT be an error and SHALL NOT contain a fabricated snapshot

#### Scenario: The trailing window is fixed and not configurable

- **WHEN** the snapshot series is requested
- **THEN** the trailing window SHALL span a single fixed length
- **AND** the window length SHALL NOT be configurable by the caller

#### Scenario: Existing project metrics contracts are preserved

- **WHEN** the snapshot read surface is added
- **THEN** the existing stage-duration, delivery-time, cost, and other project metrics surfaces SHALL remain available and unchanged
- **AND** the snapshot surface SHALL be strictly additive

#### Scenario: Unknown project returns not found

- **WHEN** a client requests the snapshot series for a project reference that does not resolve to a known project
- **THEN** the server SHALL return `404`
