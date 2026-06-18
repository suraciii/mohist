## ADDED Requirements

### Requirement: Issues carry an authored IsDraft flag defaulting to draft

The system SHALL store an authored `IsDraft` flag on each Issue. `IsDraft` is the only authored pre-start fact besides issue-level prerequisites. Newly created Issues SHALL default to `IsDraft = true` (draft) regardless of how they are created, so that explicit "mark ready" action is required before an Issue is treated as pickable. An Issue's `IsDraft` flag SHALL be settable in either direction (draft to ready, and ready back to draft) while the Issue has not started.

#### Scenario: New issue defaults to draft

- **WHEN** an Issue is created
- **THEN** the Issue SHALL have `IsDraft = true`
- **AND** the Issue SHALL remain draft until an explicit action marks it ready

#### Scenario: Mark a draft issue ready

- **WHEN** a user marks a draft Issue as ready
- **THEN** the Issue SHALL record `IsDraft = false`
- **AND** the Issue SHALL become a candidate for starting subject to its other start preconditions

#### Scenario: A ready backlog issue can be returned to draft

- **WHEN** a user marks a ready, not-yet-started Issue as draft again
- **THEN** the Issue SHALL record `IsDraft = true`
- **AND** the Issue SHALL no longer be startable until it is marked ready again

#### Scenario: IsDraft is orthogonal to execution status

- **WHEN** an Issue is draft
- **THEN** its `IssueStatus` execution lifecycle (`Backlog → InProgress → Done | Cancelled`) SHALL be unchanged
- **AND** marking an Issue ready SHALL NOT change its `IssueStatus`

### Requirement: Start readiness is derived from IsDraft and prerequisites on the Issue

The system SHALL derive whether an Issue can start as a pure function of the Issue's own state. `CanStart` SHALL be `true` if and only if the Issue is not a draft AND every issue-level prerequisite has been delivered. `CanStart` and its `Blocker` SHALL be exposed from the Issue itself and SHALL NOT be authored or set as an independent source of truth. The concrete `Blocker` SHALL be one of: `Draft`, `WaitingFor(Issue)` carrying the not-yet-delivered prerequisite Issue, or none.

#### Scenario: Draft issue is not startable with a Draft blocker

- **WHEN** an Issue has `IsDraft = true`
- **THEN** `CanStart` SHALL be `false`
- **AND** the `Blocker` SHALL be `Draft`

#### Scenario: Ready issue blocked by an undelivered prerequisite

- **WHEN** an Issue has `IsDraft = false`
- **AND** the Issue has a prerequisite Issue that is not delivered
- **THEN** `CanStart` SHALL be `false`
- **AND** the `Blocker` SHALL be `WaitingFor(Issue)` identifying that prerequisite Issue

#### Scenario: Ready issue with all prerequisites delivered is startable

- **WHEN** an Issue has `IsDraft = false`
- **AND** every prerequisite Issue for that Issue is delivered
- **THEN** `CanStart` SHALL be `true`
- **AND** the `Blocker` SHALL be none

#### Scenario: CanStart and Blocker are never authored

- **WHEN** the system persists or transmits start readiness
- **THEN** `IsDraft` and prerequisites SHALL be the authored facts
- **AND** `CanStart` and `Blocker` SHALL be derived from those facts
- **AND** the system SHALL NOT accept a directly authored `CanStart` or `Blocker` value

### Requirement: Issue Start enforces all start preconditions and reports the concrete blocker

`Issue.Start()` SHALL enforce all start preconditions before an Issue enters the pipeline. The preconditions are, in order: the Issue SHALL NOT be a draft, every issue-level prerequisite SHALL be delivered, the Issue execution status SHALL permit start, and the Issue SHALL NOT already have an active run. When start is refused, `Start()` SHALL report the concrete `Blocker` (`Draft` or `WaitingFor(Issue)`) and SHALL NOT enqueue pipeline work. An Issue that is draft SHALL be refused with a reason equivalent to "still a draft".

#### Scenario: Start a draft reports still a draft

- **WHEN** `Issue.Start()` is invoked on an Issue with `IsDraft = true`
- **THEN** the start SHALL be refused
- **AND** the reported `Blocker` SHALL be `Draft`
- **AND** the reported reason SHALL be equivalent to "still a draft"
- **AND** no pipeline work SHALL be enqueued

#### Scenario: Start an issue waiting on a prerequisite reports the waiting blocker

- **WHEN** `Issue.Start()` is invoked on a ready Issue
- **AND** the Issue has a prerequisite Issue that is not delivered
- **THEN** the start SHALL be refused
- **AND** the reported `Blocker` SHALL be `WaitingFor(Issue)` identifying that prerequisite Issue
- **AND** no pipeline work SHALL be enqueued

#### Scenario: Start a ready, unblocked issue proceeds

- **WHEN** `Issue.Start()` is invoked on an Issue with `IsDraft = false`
- **AND** every prerequisite Issue is delivered
- **AND** the Issue execution status permits start and there is no active run
- **THEN** the Issue SHALL enter the pipeline
- **AND** no start blocker SHALL be reported

#### Scenario: Start an already-running or terminal issue reports the execution blocker

- **WHEN** `Issue.Start()` is invoked on an Issue that already has an active run, or that is `Done` or `Cancelled`
- **THEN** the start SHALL be refused
- **AND** the reported reason SHALL identify the execution-status or active-run precondition
- **AND** the Issue SHALL NOT enter the pipeline a second time

### Requirement: IssueStartEligibility calculator type is retired

The system SHALL NOT model start readiness through a separate `IssueStartEligibility` calculator type. There SHALL be no `{ Startable, Reason, Message, WaitingForCompletion }` eligibility object, no stringly-typed `"ready"` / `"waiting-for-completion"` reason, and no UI `Message` string duplicating a prerequisite data array. The concrete `Blocker` (`Draft | WaitingFor(Issue) | none`) SHALL be shown directly. Exposing readiness as a shallow derived `canStart` / `blocker` pair from the Issue SHALL fully replace the eligibility object.

#### Scenario: No IssueStartEligibility type exists

- **WHEN** the start readiness of an Issue is read or transmitted
- **THEN** the system SHALL expose a derived `canStart` boolean and a `blocker` (`Draft`, `WaitingFor(Issue)`, or none)
- **AND** the system SHALL NOT expose a `startEligibility` object, a `Reason` string, or a standalone `Message` string

#### Scenario: Waiting-for-completion is expressed as a blocker case

- **WHEN** an Issue is not startable because a prerequisite Issue is not delivered
- **THEN** the readiness SHALL be expressed as the `WaitingFor(Issue)` blocker case carrying the prerequisite Issue
- **AND** the system SHALL NOT model this as a stringly-typed `"waiting-for-completion"` reason

### Requirement: Existing backlog issues migrate to ready

When `IsDraft` is introduced, existing Issues that have no recorded draft state SHALL be treated as ready (`IsDraft = false`) so that the change does not retroactively suppress already-actionable backlog work. Only newly created Issues SHALL default to draft.

#### Scenario: Pre-existing backlog issue remains startable after migration

- **WHEN** an Issue existed before `IsDraft` was introduced
- **AND** that Issue had no authored draft state
- **THEN** the Issue SHALL be treated as `IsDraft = false`
- **AND** the Issue SHALL remain startable subject to its other start preconditions

#### Scenario: Issues created after the change default to draft

- **WHEN** an Issue is created after `IsDraft` is introduced
- **THEN** the Issue SHALL default to `IsDraft = true`
- **AND** the Issue SHALL NOT be startable until explicitly marked ready
