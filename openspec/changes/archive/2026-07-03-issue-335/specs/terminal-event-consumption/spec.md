### Requirement: Both terminal events trigger epic reconciliation

Every terminal issue event SHALL trigger the owning epic's terminal-reconciliation, regardless of whether the terminal fact is a completion or a cancellation. The completion event (`com.mohist.issue.completed`) SHALL drive the epic auto-done readiness check and next-issue advance, and the cancellation event (`com.mohist.issue.cancelled`) SHALL drive the same reconciliation so that cancelling an epic's in-progress issue releases the serial in-progress slot rather than deadlocking the epic. The cancellation subscription SHALL NOT be dropped, because both terminal events clear the serial slot the epic is waiting on.

#### Scenario: A completed issue reconciles its owning epic

- **WHEN** an issue that belongs to an epic emits `com.mohist.issue.completed`
- **THEN** the owning epic grain SHALL receive a terminal-reconciliation call
- **AND** the epic SHALL evaluate auto-done readiness and next-issue advance

#### Scenario: A cancelled issue reconciles its owning epic and releases the in-progress slot

- **WHEN** an issue that is an epic's in-progress issue emits `com.mohist.issue.cancelled`
- **THEN** the owning epic grain SHALL receive a terminal-reconciliation call
- **AND** the serial in-progress slot the epic was waiting on SHALL be released so the epic does not deadlock

### Requirement: Completion produces an inbox item under the canonical id

The inbox projection SHALL subscribe to the canonical completion id (`com.mohist.issue.completed`) and SHALL produce a completion-kind inbox item when a completion event arrives, preserving the prior behavior previously keyed on the `work-completed` id. The projection SHALL resolve the project, issue, and number from the event, validate them against the loaded issue, honor the project's inbox subscription, and persist a deduplicated inbox item. Cancellation is not part of the inbox projection's terminal signal set and SHALL remain excluded.

#### Scenario: A completion event creates an inbox item

- **WHEN** a `com.mohist.issue.completed` event arrives whose project subscription is enabled
- **THEN** the inbox projection SHALL insert a completion-kind inbox item
- **AND** a replay of the same event SHALL NOT create a duplicate item

#### Scenario: A cancellation event is not projected to the inbox

- **WHEN** a `com.mohist.issue.cancelled` event arrives
- **THEN** the inbox projection SHALL NOT create an inbox item for it

### Requirement: Stage attribution recognizes the canonical terminal ids

The shared stage-attribution rule SHALL project the issue's state to `done` when it observes a `com.mohist.issue.completed` event and to `cancelled` when it observes a `com.mohist.issue.cancelled` event. A `com.mohist.issue.work-started` event SHALL continue to flip state to in-progress, and a `com.mohist.issue.reopened` event SHALL continue to flip state back to backlog. The attribution SHALL NOT recognize the legacy `work-completed` or `closed` ids.

#### Scenario: A completed event attributes the issue to done

- **WHEN** the attribution rule walks an event stream containing a `com.mohist.issue.completed` event as the latest terminal transition
- **THEN** the issue SHALL be attributed to done

#### Scenario: A cancelled event attributes the issue to cancelled

- **WHEN** the attribution rule walks an event stream containing a `com.mohist.issue.cancelled` event as the latest terminal transition
- **THEN** the issue SHALL be attributed to cancelled

### Requirement: Stage-population snapshot excludes cancelled issues under the canonical id

The daily stage-population snapshot SHALL read the canonical lifecycle event ids (`work-started`, `completed`, `cancelled`, `reopened`) when deriving each issue's attributed stage. An issue whose latest terminal transition is a cancellation (`com.mohist.issue.cancelled`) as of the snapshot day SHALL be excluded from the flow population and SHALL NOT be counted in any stage. An issue whose latest terminal transition is a completion (`com.mohist.issue.completed`) SHALL be attributed to the `done` stage.

#### Scenario: A cancelled issue is excluded from the snapshot population

- **WHEN** a snapshot day is recorded and an issue's latest terminal transition as of that day is `com.mohist.issue.cancelled`
- **THEN** the issue SHALL NOT be counted in any stage for that day

#### Scenario: A completed issue is counted in the done stage

- **WHEN** a snapshot day is recorded and an issue's latest terminal transition as of that day is `com.mohist.issue.completed`
- **THEN** the issue SHALL be attributed to the `done` stage and SHALL increment that stage's count

### Requirement: Metrics terminal bucketing classifies completed versus cancelled under the canonical ids

The dashboard completion-bucketing surface SHALL classify each terminal issue by its latest terminal event using the canonical ids: an issue whose latest terminal event is `com.mohist.issue.completed` SHALL count toward the completed total and its bucket, and an issue whose latest terminal event is `com.mohist.issue.cancelled` SHALL count toward the failed/cancelled total and its bucket. The window membership, latest-terminal-event selection, and distinct-per-bucket counting SHALL be preserved; only the id vocabulary changes.

#### Scenario: A completed terminal event counts toward the completed total

- **WHEN** an issue's latest terminal event within the window is `com.mohist.issue.completed`
- **THEN** the issue SHALL increment the completed total and its time bucket's completed count

#### Scenario: A cancelled terminal event counts toward the failed total

- **WHEN** an issue's latest terminal event within the window is `com.mohist.issue.cancelled`
- **THEN** the issue SHALL increment the failed (cancelled) total and its time bucket's failed count

### Requirement: The web timeline renders terminal events under the canonical ids

The web client's canonical event registry SHALL define the canonical terminal ids (`com.mohist.issue.cancelled` and `com.mohist.issue.completed`) and SHALL NOT define the legacy `closed` or `work-completed` ids. The event timeline SHALL render a terminal event under its canonical id, and the issue-event routing/dispatch and the typed event map SHALL key the terminal transitions on the canonical ids so live terminal events continue to invalidate issue queries and route to their handler.

#### Scenario: The canonical registry exposes the renamed terminal ids

- **WHEN** the web canonical reverse-DNS event registry is inspected
- **THEN** it SHALL contain `com.mohist.issue.cancelled` and `com.mohist.issue.completed`
- **AND** it SHALL NOT contain `com.mohist.issue.closed` or `com.mohist.issue.work-completed`

#### Scenario: A live completion event invalidates issue queries

- **WHEN** the web client receives a `com.mohist.issue.completed` event
- **THEN** the event SHALL route to the issue handler and SHALL invalidate the issue query cache

### Requirement: The web outcome handler recognizes the canonical completion id

The web reverse-DNS outcome decider SHALL recognize the canonical completion id (`com.mohist.issue.completed`) as the carrier of integration outcomes (rebase completion and merge result) that were previously keyed on the `work-completed` id. A completion event carrying a rebase payload SHALL clear any rebase-conflict state and dispatch a rebase-completed event, and a completion event carrying a merge payload SHALL surface a success toast, exactly as before the rename.

#### Scenario: A completion event with a rebase payload dispatches rebase completion

- **WHEN** the outcome decider receives a `com.mohist.issue.completed` event whose payload indicates a rebase
- **THEN** it SHALL return a handled outcome that clears rebase-conflict state and dispatches a rebase-completed event

#### Scenario: A completion event with a merge payload shows a success toast

- **WHEN** the outcome decider receives a `com.mohist.issue.completed` event whose payload indicates a merge
- **THEN** it SHALL return a handled outcome that surfaces a success toast

### Requirement: A backfill migration reconciles historical persisted terminal event rows

A one-time data migration SHALL rewrite the persisted `IssueEvents.Type` column for historical terminal rows from the legacy ids to the canonical ids: every row of type `com.mohist.issue.closed` SHALL become `com.mohist.issue.cancelled`, and every row of type `com.mohist.issue.work-completed` SHALL become `com.mohist.issue.completed`. After the migration, timeline rendering and terminal bucketing SHALL be correct across both pre-rename and post-rename data, because the two share one vocabulary. The migration SHALL be idempotent and SHALL NOT alter issue snapshot state, issue status, or the `IssueStatus` enum (Issue is state-stored and grains do not replay events, so snapshot integrity is unaffected by the event-type rewrite).

#### Scenario: Legacy closed rows are rewritten to cancelled

- **WHEN** the backfill migration runs against a database whose `IssueEvents` table contains rows of type `com.mohist.issue.closed`
- **THEN** each such row's `Type` SHALL be rewritten to `com.mohist.issue.cancelled`

#### Scenario: Legacy work-completed rows are rewritten to completed

- **WHEN** the backfill migration runs against a database whose `IssueEvents` table contains rows of type `com.mohist.issue.work-completed`
- **THEN** each such row's `Type` SHALL be rewritten to `com.mohist.issue.completed`

#### Scenario: Pre-rename and post-rename terminal data behave identically after backfill

- **WHEN** timeline rendering and terminal bucketing read terminal events after the backfill migration has run
- **THEN** issues whose terminal event was persisted before the rename and issues whose terminal event is persisted after the rename SHALL be classified identically
- **AND** no terminal issue SHALL be dropped or double-counted solely because of when its event was persisted
