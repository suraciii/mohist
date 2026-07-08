### Requirement: A nullable delivery timestamp on every event row is the sole marker of delivery progress

Each of the three event truth tables (`WorkflowRunEvents`, `IssueEvents`, `EpicEvents`) SHALL carry a nullable `DispatchedAt` column of type `DateTimeOffset`. A row SHALL be considered undelivered while `DispatchedAt IS NULL` and delivered once a non-null timestamp is set. `DispatchedAt` SHALL be the only mutable column on otherwise append-only event rows — no other delivery-tracking column, flag, or side table SHALL be introduced on these rows. All three tables are first-class peers: the column SHALL be present on every one of them with identical semantics.

#### Scenario: A newly appended event row reports as undelivered

- **WHEN** an event is appended to any of `WorkflowRunEvents`, `IssueEvents`, or `EpicEvents`
- **THEN** the persisted row SHALL have `DispatchedAt` equal to `NULL`
- **AND** reading the row back SHALL expose it as undelivered

#### Scenario: The delivery column exists uniformly across all three event tables

- **WHEN** the schema of `WorkflowRunEvents`, `IssueEvents`, and `EpicEvents` is inspected
- **THEN** each table SHALL expose a nullable `DispatchedAt` timestamp column
- **AND** the column SHALL carry identical meaning (null = undelivered) on all three tables

### Requirement: Appending an event never marks it delivered, and existing read behavior is unchanged

The existing append operation (`AppendAsync`) SHALL leave `DispatchedAt` as `NULL` — writing an event MUST NOT mark it delivered. The existing read operations (`ListAsync`, `ListIssueEventsAsync`, `ListEpicEventsAsync`) SHALL continue to return the same rows in the same order as before this change, regardless of `DispatchedAt` value; their results SHALL NOT be filtered, reordered, or shaped by delivery progress. Adding `DispatchedAt` SHALL NOT alter the set of columns the append path writes, beyond setting the new column to its `NULL` default.

#### Scenario: Append then list shows the row and it is undelivered

- **WHEN** an event is appended and the corresponding `List*Async` is then invoked for that stream
- **THEN** the row SHALL be present in the result
- **AND** the row's `DispatchedAt` SHALL be `NULL`
- **AND** the result ordering (by per-source `Id`) SHALL be unchanged from before this change

#### Scenario: Delivered and undelivered rows are returned identically by the existing list operations

- **WHEN** a stream contains both a row with `DispatchedAt` set and a row with `DispatchedAt IS NULL`
- **AND** the existing `List*Async` for that stream is invoked
- **THEN** both rows SHALL be returned in the same order they would have been before this change
- **AND** neither row SHALL be excluded or reordered on the basis of its `DispatchedAt` value

### Requirement: Delivery is marked per-row by composite key, never via a global cursor

The event storage port SHALL expose an operation to mark a specific event row as delivered, identified by its composite key `(Source, Id)` and parameterized by the delivery timestamp. Delivery tracking SHALL be per-row: the storage layer MUST NOT maintain a global cursor, offset, or high-water-mark table to represent delivery progress. A crash that falls between delivering an event and marking it SHALL leave the row still undelivered, so the next scan re-offers it (at-least-once); a global cursor SHALL NOT be introduced because it would collapse at-least-once to at-most-once under exactly that crash.

#### Scenario: Marking a row delivered sets only that row's DispatchedAt

- **WHEN** the mark-delivered operation is invoked for a specific `(Source, Id)` with a timestamp `T`
- **THEN** the row identified by that composite key SHALL have its `DispatchedAt` set to `T`
- **AND** every other row in the same table and across the other two tables SHALL retain its prior `DispatchedAt` value unchanged

#### Scenario: Re-marking an already-delivered row is harmless

- **WHEN** the mark-delivered operation is invoked for a row whose `DispatchedAt` is already non-null
- **THEN** the operation SHALL succeed
- **AND** the row SHALL remain delivered (its `DispatchedAt` SHALL be non-null)

#### Scenario: No global cursor or offset table tracks delivery progress

- **WHEN** the storage schema introduced by this change is inspected
- **THEN** there SHALL be no table, column, or row whose purpose is to record a global delivery cursor, offset, or high-water mark across event rows

### Requirement: A unified undelivered query spans all three event tables, ordered for per-stream FIFO

The event storage port SHALL expose a single undelivered query that returns rows from `WorkflowRunEvents`, `IssueEvents`, and `EpicEvents` whose `DispatchedAt IS NULL`, unified across all three tables (not three separate per-table queries the caller must stitch together). The result ordering SHALL preserve per-stream FIFO: rows SHALL be ordered by `Source` and then by per-source `Id` ascending, so that within a single stream events are offered in the order they were appended. The query SHALL exclude every row whose `DispatchedAt` is non-null.

#### Scenario: Undelivered rows from all three tables are returned by one query

- **WHEN** `WorkflowRunEvents`, `IssueEvents`, and `EpicEvents` each contain at least one row with `DispatchedAt IS NULL`
- **AND** the unified undelivered query is invoked
- **THEN** the result SHALL include the undelivered rows from all three tables

#### Scenario: Delivered rows are excluded from the undelivered query

- **WHEN** a table contains a row whose `DispatchedAt` is non-null and a row whose `DispatchedAt IS NULL`
- **AND** the unified undelivered query is invoked
- **THEN** the delivered row SHALL NOT appear in the result
- **AND** the undelivered row SHALL appear in the result

#### Scenario: The undelivered query orders rows to preserve per-stream FIFO

- **WHEN** a single `Source` has multiple undelivered rows with ascending `Id` values
- **AND** the unified undelivered query is invoked
- **THEN** those rows SHALL appear ordered by `Source` and then by `Id` ascending
- **AND** no row SHALL appear ahead of an earlier-`Id` row in the same stream

### Requirement: A partial index on each table makes the undelivered query cheap

Each of the three event tables SHALL carry a partial index scoped to rows where `DispatchedAt IS NULL`, ordered by `(Source, Id)`, so that the unified undelivered query is served by an index seek rather than a full-table scan whose cost grows with the cumulative delivered backlog. The partial index SHALL exist on `WorkflowRunEvents`, `IssueEvents`, and `EpicEvents` uniformly.

#### Scenario: Each event table has a partial index scoped to undelivered rows

- **WHEN** the indexes of `WorkflowRunEvents`, `IssueEvents`, and `EpicEvents` are inspected
- **THEN** each table SHALL have an index whose predicate is scoped to `DispatchedAt IS NULL`
- **AND** that index SHALL be keyed on `(Source, Id)`

#### Scenario: The partial index does not index the delivered backlog

- **WHEN** a row's `DispatchedAt` transitions from `NULL` to a timestamp
- **THEN** that row SHALL no longer be present in the partial index's scoped set
- **AND** the index SHALL continue to cover the remaining undelivered rows

### Requirement: A (Type, Time) composite index on the workflow and issue event tables for time-window scans

`WorkflowRunEvents` and `IssueEvents` SHALL each carry an additive `(Type, Time)` composite index. The existing `(Type, Source, Id)` index does not serve the dashboard's time-window scans, which predicate on `Type` and a `Time` range; the new index SHALL allow those predicates to be pushed into the storage layer rather than materialized client-side. `EpicEvents` SHALL NOT receive this index — it has no `(Type, Time)` dashboard consumer. This index is schema-only in this change: no existing read path is altered to consume it, and no consumer behavior changes. Predicate-pushdown of `IssueMetricsQuerier` into SQL is explicitly deferred to a follow-on of #361, because dimension materialization depends on #361 first converging producer payload shapes.

#### Scenario: The (Type, Time) index exists on the workflow and issue event tables

- **WHEN** the indexes of `WorkflowRunEvents` and `IssueEvents` are inspected
- **THEN** each table SHALL have a composite index on `(Type, Time)`

#### Scenario: The epic event table does not carry the (Type, Time) index

- **WHEN** the indexes of `EpicEvents` are inspected
- **THEN** there SHALL be no `(Type, Time)` composite index, because epic events have no dashboard consumer for that access path

#### Scenario: The (Type, Time) index is additive and changes no consumer behavior

- **WHEN** the index is added to `WorkflowRunEvents` and `IssueEvents`
- **THEN** no existing read operation SHALL change its result set, ordering, or return shape
- **AND** no consumer SHALL begin reading through the index in this change
