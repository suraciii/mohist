## Why

Once an event row lands in `WorkflowRunEvents`, `IssueEvents`, or `EpicEvents`, nothing records whether it has actually reached its handlers. There is no delivery-progress column, no way to query "what hasn't been notified yet," and no isolation zone for poison messages — a handler that exhausts retries has nowhere to park the message, so it either loops forever or gets silently dropped. This change lays the storage foundation for a durable dispatcher (epic #36): a per-row delivery timestamp on all three event tables, a partial index that makes undelivered rows cheap to find, a `DeadLetters` table for poison messages, and the storage ports that read and write them. It changes no user-visible behavior; it is the ground the dispatcher will be built on.

Note: `design/eventbus-v2.md` predates the `EpicEvents` table (added by #94) — its storage list and UNION query cover only two tables. All three event tables are first-class peers here; the delivery column, partial index, and undelivered query cover all three uniformly.

## What Changes

- Add a nullable `DispatchedAt` timestamp column to **all three** event tables (`WorkflowRunEvents`, `IssueEvents`, `EpicEvents`). `NULL` = not yet delivered; a timestamp = delivered. This is the only mutable column on otherwise append-only event rows.
- Add a partial index supporting the "undelivered" query on each of the three tables (indexed on `WHERE DispatchedAt IS NULL`, ordered by `Source, Id` for per-stream FIFO).
- Add a `DeadLetters` table isolating poison messages: each row carries the original event envelope snapshot, the failing handler identifier, the terminal error, and the attempt count. Queryable; manual replay is a later issue.
- Extend the event storage port with three operations covering all three tables: **mark a specific event row as delivered** (per-row, by composite key `(Source, Id)` — deliberately not a global cursor, to preserve at-least-once under crash), **list undelivered rows across all three tables** (unified, ordered for per-stream FIFO), and **write a dead-letter record**.
- **Merged from #298** (backlog cleanup): add a `(Type, Time)` composite index on `WorkflowRunEvents` and `IssueEvents` — the current `(Type, Source, Id)` index does not serve the dashboard's time-window scans. Schema-only, no consumer yet; dashboard-side predicate-pushdown is deferred to a #361 follow-on (depends on #361 converging producer payload shapes first). `EpicEvents` is excluded — it has no dashboard consumer for `(Type, Time)`.
- Existing event append (`AppendAsync`) and read (`List*Async`) behavior MUST be unchanged. `DispatchedAt` defaults to `NULL` on append — writing an event does not mark it delivered.

## Capabilities

- `event-delivery-progress`: Each event row across `WorkflowRunEvents`, `IssueEvents`, and `EpicEvents` SHALL carry a nullable `DispatchedAt` timestamp that is the sole marker of delivery progress. A row SHALL be undelivered while `DispatchedAt IS NULL` and delivered once a timestamp is set. Delivery SHALL be marked per-row by the row's composite key `(Source, Id)`, and MUST NOT be tracked via a global cursor or offset table (a global cursor collapses at-least-once to at-most-once when a crash falls between deliver and mark). The storage port SHALL expose a unified query for undelivered rows across all three tables — ordered to preserve per-stream FIFO — and a per-row mark-delivered operation. Appending an event SHALL leave `DispatchedAt` NULL; existing append and list behavior MUST be unchanged.
- `dead-letter-store`: A `DeadLetters` table SHALL isolate poison messages that have exhausted retries. Each record SHALL capture the original event envelope snapshot, the identifier of the failing handler, the terminal error, and the attempt count — enough to diagnose and, in a later issue, manually replay. The store SHALL expose a port to write a dead-letter record and to query existing records. Records SHALL be appended (immutable isolation, not a retry queue); automated replay is explicitly out of scope.

## Impact

- **packages/server — schema**:
  - `Infrastructure/Data/Events/WorkflowRunEventRow.cs`, `IssueEventRow.cs`, `EpicEventRow.cs` — add `DispatchedAt` (nullable `DateTimeOffset`).
  - New `Infrastructure/Data/Events/DeadLetterRow.cs` — DLQ row entity.
  - `Infrastructure/Data/Db/MohistDbContext.cs` — configure `DispatchedAt` + partial undelivered index on all three event tables; add `(Type, Time)` index on `WorkflowRunEvents` + `IssueEvents` (#298); add `DeadLetters` DbSet + entity config.
  - New EF Core migration(s) under `Infrastructure/Data/Migrations/` following `YYYYMMDDHHMMSS_PascalCaseDescription.cs`; SQLite partial-index support to be confirmed in design.md.
- **packages/server — ports & impls**:
  - `Infrastructure/Events/IEventStore.cs` — add mark-delivered + list-undelivered (covering all three tables).
  - Dead-letter port + impl (whether a separate `IDeadLetterStore` or methods on `IEventStore` is settled in design.md; the issue body frames all three operations as "event storage ports").
  - `Infrastructure/Data/Events/EventStore.cs` — implement the new delivery-progress methods.
- **packages/server — tests**:
  - `tests/.../Support/NoopEventStore.cs`, `RecordingEventStore.cs`, `InboxProjectionTestSupport.cs` — implement any new `IEventStore` members (compilation breaks otherwise).
  - New unit tests for delivery-progress and dead-letter storage (in-memory shared-cache SQLite per `design/testing.md`; `TimeProvider` injection for `DispatchedAt`, no wall-clock).
- **No web / CLI / runner changes** — pure server storage layer; no user-visible behavior change.
- **Dependencies / APIs**: no new external dependencies; no HTTP contract change.
- **Risk** (medium): schema migration across three populated tables + a new table + partial indexes; SQLite partial-index syntax and migration ordering validated in design.md. The `(Type, Time)` index is additive and non-breaking. No existing read/write path changes.
- **Verification**: `dotnet test Mohist.sln -p:SkipWebBuild=true` must pass; new storage unit tests added.
