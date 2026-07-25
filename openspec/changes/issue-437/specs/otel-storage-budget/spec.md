### Requirement: Storage usage is bounded by a budget including database, WAL and SHM

The built-in observation store SHALL be bounded by a single storage budget that SHALL cover the `otel.db` file together with its `-wal` and `-shm` sidecar files, measured as their combined byte length. The budget SHALL be exposed as a single `Mohist:Otel` configuration value, SHALL default to 1 GiB, and SHALL carry over the previously hardcoded default so existing status output is unchanged. The measurement that drives eviction SHALL use the same combined db+WAL+SHM figure that the storage probe already publishes for status, so that eviction and reported usage agree.

#### Scenario: Budget defaults to 1 GiB and is configurable

- **WHEN** the Server starts without an explicit storage budget
- **THEN** the effective storage budget SHALL be 1 GiB
- **AND** setting the `Mohist:Otel` storage budget SHALL change the budget used by both eviction and the status report

#### Scenario: The budget counts every sidecar file

- **WHEN** eviction decides whether the store is over budget
- **THEN** the decision SHALL be based on the combined size of the `.db`, `-wal` and `-shm` files
- **AND** the figure used for the eviction decision SHALL be the same combined figure reported as `usage_bytes` in status

### Requirement: Eviction follows a high and a low watermark

Eviction SHALL begin only when usage reaches the high watermark (90% of the budget) and SHALL continue until usage is reduced below the low watermark (80% of the budget). Below the low watermark, eviction SHALL stop until usage again reaches the high watermark. Eviction SHALL remove complete Traces in oldest-first order by `start_time`, deleting each Trace's header row and all of its Span rows together. Under steady ingestion the store SHALL NOT grow beyond the budget plus a single internal write block. An internal write block is the single in-flight write that crosses the high watermark before the next maintenance pass evicts; it is bounded by the OTLP request body limit currently in effect until per-write chunking lands, after which it tightens to the bounded ingest chunk. Sustained unbounded growth above the budget is a violation. When no complete Trace can be removed (the store is empty or contains only Traces still being written) and usage remains at or above the high watermark, eviction SHALL signal that reclamation is not keeping up so that admission can act.

#### Scenario: Usage crosses the high watermark

- **WHEN** combined usage reaches the high watermark during the maintenance loop
- **THEN** eviction SHALL begin removing the oldest complete Traces
- **AND** removal SHALL delete each Trace together with all of its Span rows

#### Scenario: Eviction stops at the low watermark

- **WHEN** eviction has reduced combined usage below the low watermark
- **THEN** eviction SHALL stop removing Traces
- **AND** eviction SHALL NOT resume until usage again reaches the high watermark

#### Scenario: Steady ingestion stays within one write block of the budget

- **WHEN** ingestion and eviction run together under sustained load
- **THEN** combined usage SHALL NOT exceed the budget by more than a single internal write block
- **AND** usage SHALL NOT grow without bound across maintenance passes

#### Scenario: Reclamation cannot keep up because no removable trace exists

- **WHEN** usage is at or above the high watermark and there is no complete Trace eligible for removal
- **THEN** eviction SHALL signal that reclamation is not keeping up
- **AND** it SHALL NOT delete partial Traces or Traces still receiving Spans to force reclamation

### Requirement: Space is reclaimed without a full VACUUM

After deletion, freed pages SHALL be reusable for new writes through SQLite's native free-page reuse, and the online maintenance path SHALL NOT execute a full `VACUUM` or any other long-lived exclusive rewrite of the database file. The WAL SHALL have a hard boundary maintained by an explicit truncating checkpoint (`PRAGMA wal_checkpoint(TRUNCATE)`) driven by the maintenance loop, so that the WAL does not grow without bound as Traces are deleted and reinserted. Checkpoint and eviction work SHALL be bounded per maintenance invocation and SHALL NOT hold an exclusive lock that blocks ingestion or queries for an unbounded time. A checkpoint that cannot complete because a long-running read transaction holds the WAL SHALL NOT spin, busy-wait, or block the maintenance loop indefinitely; it SHALL yield and report that reclamation is blocked.

#### Scenario: Deleted traces leave reusable free pages

- **WHEN** a maintenance pass deletes complete Traces
- **THEN** subsequent new writes SHALL reuse the freed pages rather than growing the file by the inserted volume
- **AND** the maintenance pass SHALL NOT execute a full `VACUUM`

#### Scenario: The WAL is kept bounded by a truncating checkpoint

- **WHEN** deletes and inserts run repeatedly across maintenance passes
- **THEN** the maintenance loop SHALL issue a truncating checkpoint on the WAL
- **AND** the WAL file SHALL NOT grow without bound across passes

#### Scenario: A long read transaction blocks the checkpoint

- **WHEN** the truncating checkpoint cannot reclaim WAL frames because a long-running read transaction holds them
- **THEN** the maintenance loop SHALL yield rather than busy-wait or hold an exclusive lock
- **AND** it SHALL report that reclamation is blocked so admission can react

### Requirement: Reclamation state resumes safely across restart

The current reclamation state — including whether usage was above the high watermark and whether reclamation was not keeping up — SHALL be recovered on Server restart, so that a restart into an already-over-budget store does not silently begin accepting writes that cannot be reclaimed and does not require manual cleanup to become consistent. The recovered state SHALL be seeded from a persisted local reclamation marker written during normal operation, and a missing or corrupt marker SHALL fall back conservatively (admission closed until the first maintenance probe re-derives the watermark); in both cases the watermark value itself SHALL be re-derived from bounded storage metadata on the first probe, so correctness SHALL NOT depend on the marker surviving. While observation is disabled, neither eviction nor checkpoint work SHALL run; re-enabling observation SHALL resume eviction and checkpoint from the recovered state. Recovering reclamation state SHALL NOT execute a full-table scan that scales with history; it SHALL reconstruct the watermark view from bounded storage metadata and the persisted marker.

#### Scenario: The Server restarts into an over-budget store

- **WHEN** the Server restarts with combined usage at or above the high watermark and observation enabled
- **THEN** maintenance SHALL recover that the store is over budget and resume eviction
- **AND** it SHALL NOT accept writes as if the store were healthy before reclamation state is re-established

#### Scenario: Observation is disabled while the store is over budget

- **WHEN** observation is disabled while usage is at or above the high watermark
- **THEN** neither eviction nor checkpoint SHALL run while observation is off
- **AND** re-enabling observation SHALL resume eviction and checkpoint from the recovered state without manual intervention

#### Scenario: Restart recovery cost is independent of history

- **WHEN** the Server restarts once with little history and once with a large amount of unrelated history
- **THEN** recovering reclamation state SHALL inspect the same bounded amount of metadata in both cases
- **AND** recovery SHALL NOT execute a full-table scan over the Trace or Span tables
