## Context

Mohist ships a built-in OTLP receiver backed by a SQLite observation database (`otel.db`). The store, schema, WAL-mode init, ingestion, read-only query path, storage-size probe, tri-state status, and degradation reporting all exist and are stable (delivered by #470). What is entirely absent is any retention or size bound: every received Trace is kept forever, `otel.db` grows without limit, and the write path accepts everything via `AcceptAllIngestProtectionDecision`.

The seams this change builds on are already in place and were clearly left for it:

- `IOtelMaintenanceCallback` (`OtelDiagnosticsSampler.cs:37-40`) is invoked every 10s inside the `_enabled`-gated, self-suppression-wrapped maintenance loop (`OtelDiagnosticsSampler.cs:247-264`), but **no implementation is registered**. The immediate first storage probe already runs after `ApplicationStarted` (`OtelDiagnosticsSampler.cs:137-141`).
- `IOtelStorageProbe` (`OtelDiagnosticsSampler.cs:32-67`) already returns combined `db + wal + shm` bytes — the exact figure the size budget must use.
- `IIngestProtectionDecision` (`IngestPreparation.cs:6-21`) is already consulted per-Span during `Prepare` (`TraceIngester.cs:163-167`); rejecting there classifies Spans as `ProtectionRejected`, yields an empty parsed set, returns early with `NotAttempted`, and flows through `RecordIngest` → OTLP `partial_success` + `IngestProtection` degradation.
- `RuntimeDegradationCodes` (`RuntimeObservabilityContracts.cs:54-96`) is the additive reason surface; the tri-state status contract is fixed.
- `OtelOptions` (`OtelOptions.cs`) is the narrow config object; `RuntimeValueRules.StorageBudgetBytes = 1_073_741_824` is a hardcoded constant.

Constraints (from `design/observability.md` and `design/testing.md`):

- Observation data is lossy; business data (`mohist.db`) and core `/api/health` must never be affected by observation storage pressure.
- All time logic uses an injectable `TimeProvider`; no wall clock.
- No real filesystem or DB in tests: in-memory shared-cache SQLite has no `-wal`/`-shm` sidecars, so size/WAL behavior is asserted through the already-fakeable `IOtelStorageProbe` and new fakeable reclaim/connection seams.
- Maintenance cost must scale with the work actually performed, not with unrelated history.
- The `traces`/`spans` table, column, and index names are a stable contract for the `mo otel` direct reader; deletion is pure `DELETE`, no rename.
- `Mohist:Otel:Enabled` stays default-off; request/concurrency write limits are a separate issue (#471) and are not delivered here.

Stakeholders: operators running Mohist long-term on a single host (the primary beneficiary), and #471 which will later tighten the per-write chunk that bounds the size overshoot.

## Goals / Non-Goals

**Goals:**

- Bound `otel.db` (db + WAL + SHM) at a configurable 1 GiB default and retain Traces at most a configurable 72h default, whichever binds first.
- Reclaim freed space online without a full `VACUUM` or any long exclusive rewrite, and keep the WAL bounded.
- Stop accepting new Trace writes when reclamation cannot keep up, report it through the existing rejection accounting and status surface with a distinct reason, and resume automatically when reclamation recovers.
- Recover an already-oversized observation database at startup by rebuilding it empty, with a clear log and status reason, without blocking core service startup.
- Keep all of the above testable with injected time, fake probes, and operation-count assertions.

**Non-Goals:**

- Per-OTLP-request size limits, decompression limits, or write concurrency limits (#471).
- Tail sampling, content-based filtering, or external collector management.
- A manual `mo otel prune` command; rotation is automatic and self-sustaining.
- Flipping `Mohist:Otel:Enabled` to default-on (gated on #471).
- Shrinking the business database or changing `/api/health`.

## Decisions

### D1. One maintenance callback realizes time and size eviction on the existing seam

Add a single `IOtelMaintenanceCallback` implementation (e.g. `OtelRetentionMaintenance`) registered in DI so `OtelDiagnosticsSampler` invokes it on every enabled 10s tick and on the immediate first post-start probe. Each invocation runs, in order: (1) time eviction, (2) size eviction, (3) space reclamation, then (4) re-probe and arbitrate admission. All work executes under the loop's existing `SuppressInstrumentationScope` and `_enabled` gate, so no new self-observation or off-state-execution risk is introduced.

Time eviction deletes complete Traces whose `end_time` is older than `now - RetentionMaxAge`. `end_time` is the right key because a Trace still receiving Spans is growing and must not be aged out mid-collection; times are ISO-8601 UTC text, so the cutoff is a lexicographic comparison served by the existing `idx_traces_start` index. Deletion is a complete-Trace unit: within one transaction, `DELETE FROM spans WHERE trace_id IN (SELECT trace_id FROM traces WHERE end_time < $cutoff ORDER BY start_time ASC LIMIT $batch)` then `DELETE FROM traces WHERE trace_id IN (...)` for the same id set, so no orphan Span or Trace rows survive. The batch limit (an internal constant, not user-tunable) bounds per-tick work; remaining aged Traces are removed on subsequent ticks, which is the resumable behavior.

Size eviction runs only when the latest probe reports usage at or above the high watermark (90%). It removes the oldest complete Traces (by `start_time ASC`) in the same bounded batch shape until either usage drops below the low watermark (80%) or no removable Trace remains. "Removable" excludes Traces whose `end_time` is at or after `now` (still being collected); a store that is full of only such Traces cannot be reclaimed. After each batch the loop re-probes via `IOtelStorageProbe` (the same combined db+WAL+SHM figure status reports) so eviction and status agree.

The callback then publishes the reclamation/admission outcome to a singleton `OtelStorageGuard` (D3): if usage is still at/above high watermark after eviction and reclamation, the guard closes admission; if usage is below low watermark, the guard opens it.

Alternatives considered:

- *Two callbacks (time, size).* Rejected: they share one connection, one probe, one transaction boundary, and one admission outcome; splitting them would duplicate the probe and complicate the single-transaction complete-Trace invariant.
- *Delete by `start_time` for time eviction too.* Rejected: a long-running Trace whose start is old but whose latest Span is recent would be deleted while still growing. `end_time` is the correct age key.

### D2. Space is reclaimed by incremental vacuum plus a truncating checkpoint, never by full VACUUM

This is the central technical decision. Deleting rows creates free pages, but without either `VACUUM` or `auto_vacuum` the main `.db` file never shrinks — free pages are only reused for new inserts. If the size budget is measured against file size (which the spec requires: db + WAL + SHM), then a file that has ever held ~1 GiB of live data stays at ~1 GiB on disk forever, and size eviction measured against file size could never reduce usage below the low watermark — it would evict to empty, fail to reclaim, and close admission permanently. A full online `VACUUM` is forbidden by the spec because it rewrites the entire file under an exclusive lock.

The resolution is SQLite's incremental auto-vacuum:

- `OtelDb.EnsureInitialized` sets `PRAGMA journal_mode=WAL` (already present) **and** `PRAGMA auto_vacuum = INCREMENTAL` before the `CREATE TABLE IF NOT EXISTS` statements. `auto_vacuum` is a database-header flag that only takes effect when set before any object is created, so this activates incremental vacuum for every freshly created `otel.db`.
- The maintenance callback issues `PRAGMA incremental_vacuum($pages)` after eviction. This moves live pages out of the file's tail into earlier free pages and truncates the file by up to `$pages` pages — bounded, incremental work, **not** a full-file exclusive rewrite. `$pages` is an internal per-tick cap so reclamation cost is bounded.
- The maintenance callback issues `PRAGMA wal_checkpoint(TRUNCATE)` to cap the WAL at the checkpoint boundary, giving the WAL its hard edge.

Together these let the measured db+WAL+SHM figure actually decrease after eviction, so the high/low watermark loop can converge and admission can reopen. The "one internal write block" overshoot is the single in-flight write that crosses the high watermark before the next tick; its tightness depends on #471's per-write chunking, which is why default-on is gated on #471.

Existing databases created before this change have `auto_vacuum = 0` (the SQLite default); for them `incremental_vacuum` is a no-op and the file cannot shrink incrementally. Such a database is exactly the oversized-at-startup case handled by D5's rebuild: once it cannot be reclaimed under the budget it is rebuilt as a fresh `auto_vacuum = INCREMENTAL` database (discarding lossy observation data). In practice the OTel subsystem is default-off and there is no large installed base of `otel.db` files, so this is a defensive path, not a common migration.

Alternatives considered:

- *Periodic full `VACUUM`.* Rejected by the spec: exclusive full-file rewrite, unbounded duration, blocks ingestion and queries.
- *Measure live-data bytes instead of file size.* Rejected by the spec: the budget explicitly includes the db + WAL + SHM files.
- *`auto_vacuum = FULL`.* Rejected: full auto-vacuum runs at every commit and cannot be throttled; incremental gives bounded, schedulable reclamation.

### D3. A budget-aware ingest decision shares one admission state and a distinct degradation reason

Replace `AcceptAllIngestProtectionDecision` (`IngestPreparation.cs:17`) with a budget-aware `IIngestProtectionDecision` backed by a singleton `OtelStorageGuard`. The guard owns one piece of mutable admission state — whether writes are currently closed because reclamation is not keeping up — set and cleared by the maintenance callback (D1) and read by the decision. Because the decision is state-based, it collapses to per-write: `Decide` returns `Reject()` for every Span while admission is closed and `Accept()` otherwise, so a closed store classifies the whole batch as `ProtectionRejected` and returns early with `NotAttempted` through the existing `IngestBatch` path.

Rejected Span counts still flow through the existing `RecordIngest` → `TelemetryRejected` accounting and produce OTLP `partial_success` with a non-retry instruction, satisfying the partial-success and counter requirements without new write-path code. To satisfy the spec's "distinct reason identifying storage-budget exhaustion", add one degradation code `storage_budget_exhausted` valid for the existing `IngestProtection` source (`RuntimeDegradationContracts.IsValidFor`), with a bounded default message. The guard publishes this code directly via the existing degradation-seed mechanism when it closes admission and clears it (subject to the existing protection window) when it reopens; because it shares the `IngestProtection` source with the generic rejection signal, it does not change the tri-state contract or let one subsystem clear another. The guard publishes the specific code after the ingest outcome so the more specific reason dominates `latest_degradation`.

Admission never blocks: `Decide` is a volatile read; a closed store refuses promptly without waiting on reclamation, so the request thread, Workflow scheduler, and Runner communication are unaffected.

Alternatives considered:

- *Reject at the HTTP route before parsing.* Rejected: it would bypass the existing outcome accounting and OTLP `partial_success` encoding that already correctly handles rejection.
- *A new top-level degradation source.* Rejected: it would change the fixed five-source contract established by #470 and could let an unrelated source clear the over-budget cause.

### D4. Configuration promotes the budget and adds the retention age, nothing else

Add two properties to `OtelOptions` (`Mohist:Otel`): `StorageBudgetBytes` (default `1_073_741_824`, the current `RuntimeValueRules.StorageBudgetBytes` value) and `RetentionMaxAge` (default `72h`). Keep `RuntimeValueRules.StorageBudgetBytes` as the default constant so the value already reported by `/otel/api/status` is unchanged. Wire both into the maintenance callback, the guard, and `RuntimeObservability`'s budget (already ctor-injected). No further user-tunable knobs (high/low watermark ratios, batch size, incremental-vacuum page cap) are exposed; they are internal constants, matching the existing "schema is intentionally narrow" stance.

### D5. Startup recovery rebuilds an oversized database without blocking core services

After `ApplicationStarted`, the diagnostics sampler's first tick probes storage. If combined usage exceeds the safe budget (the configured `StorageBudgetBytes`), the recovery path rebuilds an empty observation database: it clears the SQLite connection pool for the old file (`SqliteConnection.ClearPool`/`ClearAllPools`), deletes the old `.db`, `-wal`, and `-shm` through `IFileSystem`, and lets `OtelDb.EnsureInitialized` recreate a fresh schema with `auto_vacuum = INCREMENTAL`. It emits one structured "observation data reset" log and publishes a data-reset degradation reason (additive, on a source consistent with the existing contract). Because observation data is lossy by design, discarding it is acceptable; the business database and `/api/health` are untouched, and a rebuild failure is contained — it publishes a storage degradation and leaves the store unusable for observation without preventing the core Server from being reachable.

This runs in the maintenance loop, not on the core startup path, so core services become reachable within their normal bound regardless of oversized-DB size. The detection and connection-clearing work is bounded by file-length reads and pool clearing; it does not scan or iterate Traces or Spans, so its cost is independent of how much history the oversized file holds.

During the window between listener bind and the first probe, admission starts **closed** (D6), so no write reaches the oversized database before recovery has run.

Alternatives considered:

- *Online `VACUUM` at startup to shrink an oversized DB.* Rejected by the spec and by the core-startup-bound requirement.
- *Rebuild any pre-existing DB to gain `auto_vacuum`.* Rejected: it discards data unnecessarily for under-budget databases; only databases that cannot be reclaimed under the budget are rebuilt.

### D6. Reclamation state is re-derived, not persisted; admission starts closed

Rather than persist a reclamation/admission marker, the guard starts with admission **closed** on every startup and is opened only after the first maintenance probe confirms the store is below the low watermark and reclaimable. This re-derives the full watermark view from the bounded storage probe (file sizes) on each start, so:

- A restart into an over-budget store never accepts writes as if healthy — admission is closed until eviction+reclamation bring it below the low watermark.
- A restart into a healthy store opens admission after the immediate first probe.
- Restart cost is bounded (one probe, no full-table scan) and independent of history.

This is a strictly stronger guarantee than a persisted "healthy" marker, which could lie across a crash. It satisfies the spec's safe-continue and bounded-restart-cost scenarios; the spec's "persisted reclamation state" wording is realized by re-derivation from bounded metadata plus the conservative initial-closed rule, which is simpler and safer. If a future requirement needs admission state to survive a probe failure, a minimal sidecar marker can be added without changing this invariant.

### D7. Testability through fakes, injected time, and operation counts

- **Time eviction**: drive `FakeTimeProvider` past `RetentionMaxAge` on an `InMemoryOtelDb`, assert complete-Trace deletion (Trace row + all its Span rows gone), batch bounding, and resumption after interruption. No wall clock.
- **Size eviction and reclamation**: in-memory SQLite has no WAL/SHM sidecars and its file length is not a real on-disk file, so feed usage through a fake `IOtelStorageProbe` and assert the high/low-watermark decisions and admission transitions. Exercise `incremental_vacuum` and `wal_checkpoint` through a fakeable reclaim seam (or assert the commands are issued) rather than measuring real file lengths.
- **Blocked checkpoint**: a fake probe/reclaim that reports "cannot reclaim" drives the "admission closed, degradation published" path without a real long-running reader.
- **Restart recovery**: a fake probe returning usage above the budget drives the rebuild decision; a fake `IFileSystem` + connection-pool seam asserts the old files are removed and a fresh DB is initialized, with core reachability asserted independently.
- **Cost independence**: run the same eviction/recovery once with little and once with much unrelated history and assert the same bounded statement count — no `COUNT(*)`, no full-table scan.

## Risks / Trade-offs

- `[Main .db file cannot shrink without auto_vacuum] ->` Enable `auto_vacuum = INCREMENTAL` on fresh databases (D2) and rebuild databases that predate it once they are unreclaimable (D5); the rebuild discards only lossy observation data.
- `[Full VACUUM temptation under pressure] ->` The maintenance path issues only `incremental_vacuum($pages)` and `wal_checkpoint(TRUNCATE)`; a test locks that no `VACUUM` statement is ever issued on the online path.
- `[Long read transaction blocks the truncating checkpoint] ->` The checkpoint yields rather than busy-waiting; the guard closes admission and publishes the over-budget reason until the checkpoint completes.
- `[Existing DB predating auto_vacuum cannot incrementally shrink] ->` Treated as the oversized-at-startup case and rebuilt (D5); acceptable because observation data is lossy and the OTel subsystem is default-off with no large installed base.
- [`auto_vacuum` page-map overhead] ->` Bounded and acceptable for an observation store; outweighed by the ability to reclaim without a full VACUUM.
- `[Size overshoot is only as tight as the in-flight write] ->` Admission closes at the high watermark; the "one internal write block" bound becomes tight once #471 delivers write chunking. Default-on is gated on #471 for this reason.
- `[Double-signaling over-budget and generic rejection] ->` Both share the `IngestProtection` source so neither can clear the other; the specific `storage_budget_exhausted` code is published after the ingest outcome so it dominates `latest_degradation`.
- `[Rebuild loses observation data] ->` By design: observation data is lossy, the rebuild logs and reports a data-reset reason, and core data is never touched.
- `[Schema contract erosion] ->` Deletion is pure `DELETE FROM traces/spans`; no existing table, column, or index name changes. Any new internal metadata lives outside the stable reader surface.
- `[Maintenance cost grows with history] ->` Bounded batch deletes on indexed `end_time`/`start_time`, bounded `incremental_vacuum($pages)`, and operation-count tests lock cost independence.

## Migration Plan

1. Add `StorageBudgetBytes` and `RetentionMaxAge` to `OtelOptions` (defaults preserve current behavior); wire into `RuntimeObservability` and the new components.
2. Set `auto_vacuum = INCREMENTAL` in `OtelDb.EnsureInitialized` before table creation; add the bounded delete, `incremental_vacuum`, and `wal_checkpoint(TRUNCATE)` helpers.
3. Implement the single retention/budget maintenance callback and register it as `IOtelMaintenanceDecision`; implement `OtelStorageGuard` and the budget-aware `IIngestProtectionDecision`; add the `storage_budget_exhausted` and data-reset degradation codes.
4. Implement the startup recovery/rebuild path on the first maintenance tick.
5. Add spec and unit tests across all four capabilities using `FakeTimeProvider`, `InMemoryOtelDb`, fake `IOtelStorageProbe`, and operation-count assertions; run `npm test`.

There is no business-database migration and no change to `/api/health`. Rollback is simply disabling `Mohist:Otel:Enabled`, which stops collection, storage probing, and all maintenance work. Because this change depends on the publication contract from #470 and is a dependency of #471's default-on gate, rollback order is #471-first, then this change, then #470.

## Open Questions

- Whether to surface the current high/low-watermark state and the data-reset reason as explicit fields on `/otel/api/status` (additive) or keep them only inside `latest_degradation`; leaning toward the latter to avoid expanding the status contract, pending operator feedback.
- Whether a future hardening issue should persist a minimal admission marker to survive a probe failure across restarts; not needed for correctness given D6's conservative initial-closed rule.
